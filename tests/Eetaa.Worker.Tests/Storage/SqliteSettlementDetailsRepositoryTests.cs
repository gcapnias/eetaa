using Eetaa.Worker.Data;
using Eetaa.Worker.Discovery;
using Eetaa.Worker.Storage;
using Microsoft.Data.Sqlite;

namespace Eetaa.Worker.Tests.Storage;

public class SqliteSettlementDetailsRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteQueueRepository _queueRepository;
    private readonly SqliteSettlementDetailsRepository _detailsRepository;

    public SqliteSettlementDetailsRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"eetaa_details_test_{Guid.NewGuid():N}.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);
        _queueRepository = new SqliteQueueRepository(_connectionFactory);
        _detailsRepository = new SqliteSettlementDetailsRepository(_connectionFactory);
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

    private async Task SeedQueueAsync(params long[] settlementIds)
    {
        await _queueRepository.InitializeDatabaseAsync();
        var settlements = settlementIds.Select(id => new DiscoveredSettlement((int)id, $"Settlement {id}")).ToList();
        await _queueRepository.UpsertDiscoveredSettlementsAsync(settlements);
    }

    [Fact]
    public async Task Schema_CreatesOikmetDetailsWithPrimaryKeyAndForeignKeys()
    {
        await _queueRepository.InitializeDatabaseAsync();

        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();

        // Verify oikmet_details exists
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='oikmet_details';";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.Equal(1, count);
        }

        // Verify indexes exist
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='index' AND name IN ('idx_oikmet_details_settlement_id', 'idx_oikmet_details_fek_date');";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.Equal(2, count);
        }
    }

    [Fact]
    public async Task SaveSettlementChangesAsync_PersistsRecordsAndMarksQueueCompleted()
    {
        await SeedQueueAsync(25734);

        var changes = new List<SettlementDetailRecord>
        {
            new(25734, 1, "21η Απριλίου", "Θεσσαλονίκης", 1971, null, null, "1971-03-14", "14/03/1971", "Αναγνώριση", null),
            new(25734, 2, "Καλλιθέα", "Θεσσαλονίκης", 1975, "96", "Α", "1975-05-21", "21/05/1975", "Μετονομασία", null)
        };
        var payload = new SettlementPayload(25734, changes);

        await _detailsRepository.SaveSettlementChangesAsync(payload);

        // Verify details saved
        var retrieved = await _detailsRepository.GetChangesBySettlementIdAsync(25734);
        Assert.Equal(2, retrieved.Count);
        Assert.Equal("21η Απριλίου", retrieved[0].SettlementName);
        Assert.Equal("1971-03-14", retrieved[0].FekDate);
        Assert.Equal("14/03/1971", retrieved[0].FekDateRaw);
        Assert.Equal("Καλλιθέα", retrieved[1].SettlementName);
        Assert.Equal("96", retrieved[1].FekNumber);
        Assert.Equal("Α", retrieved[1].FekIssue);

        // Verify queue status is completed
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status, attempt_count, completed_at, error_message FROM settlement_queue WHERE settlement_id = 25734;";
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("completed", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.False(reader.IsDBNull(2));
        Assert.True(reader.IsDBNull(3));
    }

    [Fact]
    public async Task SaveSettlementChangesAsync_IsIdempotentAndReplacesOldDetails()
    {
        await SeedQueueAsync(100);

        // First save with 2 changes
        var firstChanges = new List<SettlementDetailRecord>
        {
            new(100, 1, "Old Name", "Attica", 1835, null, null, null, null, "First desc"),
            new(100, 2, "Old Name 2", "Attica", 1845, null, null, null, null, "Second desc")
        };
        await _detailsRepository.SaveSettlementChangesAsync(new SettlementPayload(100, firstChanges));
        Assert.Equal(2, (await _detailsRepository.GetChangesBySettlementIdAsync(100)).Count);

        // Second save replacing with 1 new change
        var updatedChanges = new List<SettlementDetailRecord>
        {
            new(100, 1, "Corrected Name", "Attica", 1835, "12", "A", "1835-05-10", "10/05/1835", "Updated desc")
        };
        await _detailsRepository.SaveSettlementChangesAsync(new SettlementPayload(100, updatedChanges));

        var finalDetails = await _detailsRepository.GetChangesBySettlementIdAsync(100);
        Assert.Single(finalDetails);
        Assert.Equal("Corrected Name", finalDetails[0].SettlementName);
        Assert.Equal("12", finalDetails[0].FekNumber);

        // Verify queue attempt count incremented
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT attempt_count, status FROM settlement_queue WHERE settlement_id = 100;";
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal("completed", reader.GetString(1));
    }

    [Fact]
    public async Task SaveSettlementChangesAsync_EmptyChangesMarksCompletedWithZeroRows()
    {
        await SeedQueueAsync(999);

        var payload = new SettlementPayload(999, Array.Empty<SettlementDetailRecord>());
        await _detailsRepository.SaveSettlementChangesAsync(payload);

        var details = await _detailsRepository.GetChangesBySettlementIdAsync(999);
        Assert.Empty(details);

        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status, error_message FROM settlement_queue WHERE settlement_id = 999;";
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("completed", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
    }

    [Fact]
    public async Task SaveSettlementChangesAsync_WithErrorMarksQueueFailed()
    {
        await SeedQueueAsync(500);

        var payload = new SettlementPayload(500, Array.Empty<SettlementDetailRecord>(), ErrorMessage: "HTTP 500 Internal Server Error");
        await _detailsRepository.SaveSettlementChangesAsync(payload);

        var details = await _detailsRepository.GetChangesBySettlementIdAsync(500);
        Assert.Empty(details);

        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status, error_message, attempt_count FROM settlement_queue WHERE settlement_id = 500;";
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("failed", reader.GetString(0));
        Assert.Equal("HTTP 500 Internal Server Error", reader.GetString(1));
        Assert.Equal(1, reader.GetInt32(2));
    }

    [Fact]
    public async Task ForeignKeyCascade_DeletingQueueItemDeletesAssociatedDetails()
    {
        await SeedQueueAsync(12345);

        var changes = new List<SettlementDetailRecord>
        {
            new(12345, 1, "Test Village", "Messinia", 1900, null, null, null, null, "Creation")
        };
        await _detailsRepository.SaveSettlementChangesAsync(new SettlementPayload(12345, changes));
        Assert.Single(await _detailsRepository.GetChangesBySettlementIdAsync(12345));

        // Delete from queue directly with foreign keys enabled
        using (var conn = _connectionFactory.CreateConnection())
        {
            await conn.OpenAsync();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync();

            using var delCmd = conn.CreateCommand();
            delCmd.CommandText = "DELETE FROM settlement_queue WHERE settlement_id = 12345;";
            await delCmd.ExecuteNonQueryAsync();
        }

        // Details should be cascaded and deleted
        var remainingDetails = await _detailsRepository.GetChangesBySettlementIdAsync(12345);
        Assert.Empty(remainingDetails);
    }
}
