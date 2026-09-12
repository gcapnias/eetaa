using Eetaa.Worker.Data;
using Eetaa.Worker.Storage;

using Microsoft.Data.Sqlite;

namespace Eetaa.Worker.Tests.Storage;

public class SqliteLocalAuthorityDetailsRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteQueueRepository _queueRepository;
    private readonly SqliteLocalAuthorityDetailsRepository _detailsRepository;

    public SqliteLocalAuthorityDetailsRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"eetaa_la_details_test_{Guid.NewGuid():N}.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);
        _queueRepository = new SqliteQueueRepository(_connectionFactory);
        _detailsRepository = new SqliteLocalAuthorityDetailsRepository(_connectionFactory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            var wal = _dbPath + "-wal";
            if (File.Exists(wal)) File.Delete(wal);
            var shm = _dbPath + "-shm";
            if (File.Exists(shm)) File.Delete(shm);
        }
        catch
        {
            // Ignore temp file cleanup exceptions
        }
    }

    private async Task SeedLocalAuthorityQueueAsync(params long[] authorityIds)
    {
        await _queueRepository.InitializeDatabaseAsync();

        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();

        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO local_authority_queue (authority_id, name_hint, status)
            VALUES (@authority_id, @name_hint, 'pending');
        ";
        var idParam = cmd.Parameters.Add("@authority_id", SqliteType.Integer);
        var nameParam = cmd.Parameters.Add("@name_hint", SqliteType.Text);

        foreach (var id in authorityIds)
        {
            idParam.Value = id;
            nameParam.Value = $"Authority {id}";
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    [Fact]
    public async Task Schema_CreatesDkmetDetailsWithCompositePrimaryKeyAndForeignKeys()
    {
        await _queueRepository.InitializeDatabaseAsync();

        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();

        // Verify foreign keys are enabled (1)
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA foreign_keys;";
            var fk = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.Equal(1, fk);
        }

        // Verify dkmet_details exists
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='dkmet_details';";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.Equal(1, count);
        }

        // Verify indexes exist
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='index' AND name IN ('idx_dkmet_details_authority_id', 'idx_dkmet_details_fek_date');";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.Equal(2, count);
        }
    }

    [Fact]
    public async Task SaveLocalAuthorityChangesAsync_PersistsRecordsAndMarksQueueCompleted()
    {
        await SeedLocalAuthorityQueueAsync(1001);

        var changes = new List<LocalAuthorityDetailRecord>
        {
            new(1001, 1, AuthorityType.Community, "Αγίας Βαρβάρας", "Ηρακλείου", 1925, "27", "Α", "1925-01-31", "31/01/1925", "Σύσταση της κοινότητας", null),
            new(1001, 2, AuthorityType.Municipality, "Αγίας Βαρβάρας", "Ηρακλείου", 1997, "244", "Α", "1997-12-04", "04/12/1997", "Σύσταση του δήμου", null)
        };
        var payload = new LocalAuthorityPayload(1001, changes);

        await _detailsRepository.SaveLocalAuthorityChangesAsync(payload);

        // Verify details saved
        var retrieved = await _detailsRepository.GetChangesByAuthorityIdAsync(1001);
        Assert.Equal(2, retrieved.Count);
        Assert.Equal(AuthorityType.Community, retrieved[0].AuthorityType);
        Assert.Equal("Αγίας Βαρβάρας", retrieved[0].AuthorityName);
        Assert.Equal("1925-01-31", retrieved[0].FekDate);
        Assert.Equal("31/01/1925", retrieved[0].FekDateRaw);

        Assert.Equal(AuthorityType.Municipality, retrieved[1].AuthorityType);
        Assert.Equal("244", retrieved[1].FekNumber);
        Assert.Equal("Α", retrieved[1].FekIssue);

        // Verify raw database row has ota_type as 'Κ' and 'Δ'
        using (var conn = _connectionFactory.CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT change_order, ota_type FROM dkmet_details WHERE authority_id = 1001 ORDER BY change_order ASC;";
            using var reader = await cmd.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());
            Assert.Equal("Κ", reader.GetString(1));

            Assert.True(await reader.ReadAsync());
            Assert.Equal("Δ", reader.GetString(1));
        }

        // Verify queue status is completed
        using (var conn = _connectionFactory.CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT status, attempt_count, completed_at, error_message FROM local_authority_queue WHERE authority_id = 1001;";
            using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("completed", reader.GetString(0));
            Assert.Equal(1, reader.GetInt32(1));
            Assert.False(reader.IsDBNull(2));
            Assert.True(reader.IsDBNull(3));
        }
    }

    [Fact]
    public async Task SaveLocalAuthorityChangesAsync_IsIdempotentAndReplacesOldDetails()
    {
        await SeedLocalAuthorityQueueAsync(100);

        // First save with 2 changes
        var firstChanges = new List<LocalAuthorityDetailRecord>
        {
            new(100, 1, AuthorityType.Community, "Old Comm", "Attica", 1835, null, null, null, null, "First desc"),
            new(100, 2, AuthorityType.Community, "Old Comm 2", "Attica", 1845, null, null, null, null, "Second desc")
        };
        await _detailsRepository.SaveLocalAuthorityChangesAsync(new LocalAuthorityPayload(100, firstChanges));
        Assert.Equal(2, (await _detailsRepository.GetChangesByAuthorityIdAsync(100)).Count);

        // Second save replacing with 1 new change
        var updatedChanges = new List<LocalAuthorityDetailRecord>
        {
            new(100, 1, AuthorityType.Municipality, "Corrected Muni", "Attica", 1835, "12", "A", "1835-05-10", "10/05/1835", "Updated desc")
        };
        await _detailsRepository.SaveLocalAuthorityChangesAsync(new LocalAuthorityPayload(100, updatedChanges));

        var finalDetails = await _detailsRepository.GetChangesByAuthorityIdAsync(100);
        Assert.Single(finalDetails);
        Assert.Equal(AuthorityType.Municipality, finalDetails[0].AuthorityType);
        Assert.Equal("Corrected Muni", finalDetails[0].AuthorityName);
        Assert.Equal("12", finalDetails[0].FekNumber);

        // Verify queue attempt count incremented
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT attempt_count, status FROM local_authority_queue WHERE authority_id = 100;";
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal("completed", reader.GetString(1));
    }

    [Fact]
    public async Task SaveLocalAuthorityChangesAsync_EmptyChangesMarksCompletedWithZeroRows()
    {
        await SeedLocalAuthorityQueueAsync(999);

        var payload = new LocalAuthorityPayload(999, Array.Empty<LocalAuthorityDetailRecord>());
        await _detailsRepository.SaveLocalAuthorityChangesAsync(payload);

        var details = await _detailsRepository.GetChangesByAuthorityIdAsync(999);
        Assert.Empty(details);

        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status, error_message FROM local_authority_queue WHERE authority_id = 999;";
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("completed", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
    }

    [Fact]
    public async Task SaveLocalAuthorityChangesAsync_WithErrorMarksQueueFailed()
    {
        await SeedLocalAuthorityQueueAsync(500);

        var payload = new LocalAuthorityPayload(500, Array.Empty<LocalAuthorityDetailRecord>(), ErrorMessage: "HTTP 502 Bad Gateway");
        await _detailsRepository.SaveLocalAuthorityChangesAsync(payload);

        var details = await _detailsRepository.GetChangesByAuthorityIdAsync(500);
        Assert.Empty(details);

        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status, error_message, attempt_count FROM local_authority_queue WHERE authority_id = 500;";
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("failed", reader.GetString(0));
        Assert.Equal("HTTP 502 Bad Gateway", reader.GetString(1));
        Assert.Equal(1, reader.GetInt32(2));
    }

    [Fact]
    public async Task ForeignKeyCascade_DeletingQueueItemDeletesAssociatedDkmetDetails()
    {
        await SeedLocalAuthorityQueueAsync(12345);

        var changes = new List<LocalAuthorityDetailRecord>
        {
            new(12345, 1, AuthorityType.Community, "Test Authority", "Messinia", 1900, null, null, null, null, "Creation")
        };
        await _detailsRepository.SaveLocalAuthorityChangesAsync(new LocalAuthorityPayload(12345, changes));
        Assert.Single(await _detailsRepository.GetChangesByAuthorityIdAsync(12345));

        // Delete from queue directly with foreign keys enabled via connection factory
        using (var conn = _connectionFactory.CreateConnection())
        {
            await conn.OpenAsync();
            using var delCmd = conn.CreateCommand();
            delCmd.CommandText = "DELETE FROM local_authority_queue WHERE authority_id = 12345;";
            await delCmd.ExecuteNonQueryAsync();
        }

        // Details should be cascaded and deleted
        var remainingDetails = await _detailsRepository.GetChangesByAuthorityIdAsync(12345);
        Assert.Empty(remainingDetails);
    }
}
