using Eetaa.Worker.Data;
using Eetaa.Worker.Discovery;

using Microsoft.Data.Sqlite;

namespace Eetaa.Worker.Tests.Data;

public class SqliteLocalAuthorityQueueRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteLocalAuthorityQueueRepository _repository;

    public SqliteLocalAuthorityQueueRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"eetaa_la_test_{Guid.NewGuid():N}.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);
        _repository = new SqliteLocalAuthorityQueueRepository(_connectionFactory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
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

    [Fact]
    public async Task InitializeDatabaseAsync_ConfiguresWalAndCreatesLocalAuthorityQueueTable()
    {
        await _repository.InitializeDatabaseAsync();

        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();

        // Verify WAL mode
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode;";
            var mode = (string?)await cmd.ExecuteScalarAsync();
            Assert.Equal("wal", mode, ignoreCase: true);
        }

        // Verify foreign keys ON
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA foreign_keys;";
            var fk = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.Equal(1, fk);
        }

        // Verify local_authority_queue table exists
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='local_authority_queue';";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.Equal(1, count);
        }

        // Verify idx_local_authority_queue_status index exists
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='index' AND name='idx_local_authority_queue_status';";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.Equal(1, count);
        }
    }

    [Fact]
    public async Task UpsertDiscoveredAuthoritiesAsync_InsertsNewAuthoritiesAndPreservesExisting()
    {
        await _repository.InitializeDatabaseAsync();

        var initial = new List<DiscoveredLocalAuthority>
        {
            new(1001, "Δ. Αθηναίων"),
            new(1002, "Δ. Πειραιώς")
        };

        var inserted = await _repository.UpsertDiscoveredAuthoritiesAsync(initial);
        Assert.Equal(2, inserted);

        var totalCount = await _repository.GetCountAsync();
        Assert.Equal(2, totalCount);

        // Simulate item 1001 moving to completed
        using (var conn = _connectionFactory.CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE local_authority_queue SET status = 'completed' WHERE authority_id = 1001;";
            await cmd.ExecuteNonQueryAsync();
        }

        // Upsert again with duplicate 1001 and new item 1003
        var nextBatch = new List<DiscoveredLocalAuthority>
        {
            new(1001, "Δ. Αθηναίων (Alias)"),
            new(1003, "Κ. Αχαρνών")
        };

        var newlyInserted = await _repository.UpsertDiscoveredAuthoritiesAsync(nextBatch);
        Assert.Equal(1, newlyInserted);

        // Check total count is now 3
        Assert.Equal(3, await _repository.GetCountAsync());

        // Check item 1001 still has status 'completed'
        using (var conn = _connectionFactory.CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT status, name_hint FROM local_authority_queue WHERE authority_id = 1001;";
            using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("completed", reader.GetString(0));
            Assert.Equal("Δ. Αθηναίων", reader.GetString(1));
        }
    }

    [Fact]
    public async Task ResetInFlightProcessingAsync_ResetsProcessingToPending()
    {
        await _repository.InitializeDatabaseAsync();

        var batch = new List<DiscoveredLocalAuthority>
        {
            new(2001, "Δήμος 2001"),
            new(2002, "Κοινότητα 2002")
        };
        await _repository.UpsertDiscoveredAuthoritiesAsync(batch);

        using (var conn = _connectionFactory.CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE local_authority_queue SET status = 'processing' WHERE authority_id = 2001;";
            await cmd.ExecuteNonQueryAsync();
        }

        var resetCount = await _repository.ResetInFlightProcessingAsync();
        Assert.Equal(1, resetCount);

        using (var conn = _connectionFactory.CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT status FROM local_authority_queue WHERE authority_id = 2001;";
            var status = (string?)await cmd.ExecuteScalarAsync();
            Assert.Equal("pending", status);
        }
    }

    [Fact]
    public async Task GetPendingAuthoritiesAsync_And_StatusTransitions_WorkAsExpected()
    {
        await _repository.InitializeDatabaseAsync();

        var batch = new List<DiscoveredLocalAuthority>
        {
            new(3001, "Δήμος 3001"),
            new(3002, "Δήμος 3002"),
            new(3003, "Κοινότητα 3003")
        };
        await _repository.UpsertDiscoveredAuthoritiesAsync(batch);

        var pending = await _repository.GetPendingAuthoritiesAsync(includeFailed: false, forceAll: false);
        Assert.Equal(3, pending.Count);

        var marked = await _repository.MarkProcessingAsync(3001);
        Assert.True(marked);

        pending = await _repository.GetPendingAuthoritiesAsync(includeFailed: false, forceAll: false);
        Assert.Equal(2, pending.Count);

        await _repository.MarkFailedAsync(3001, "Network timeout 504");
        var failedCount = await _repository.GetCountByStatusAsync("failed");
        Assert.Equal(1, failedCount);

        var pendingOrFailed = await _repository.GetPendingAuthoritiesAsync(includeFailed: true, forceAll: false);
        Assert.Equal(3, pendingOrFailed.Count);
    }
}
