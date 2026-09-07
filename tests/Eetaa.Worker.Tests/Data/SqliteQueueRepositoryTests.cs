using Eetaa.Worker.Data;
using Eetaa.Worker.Discovery;
using Microsoft.Data.Sqlite;

namespace Eetaa.Worker.Tests.Data;

public class SqliteQueueRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteQueueRepository _repository;

    public SqliteQueueRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"eetaa_test_{Guid.NewGuid():N}.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);
        _repository = new SqliteQueueRepository(_connectionFactory);
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
    public async Task InitializeDatabaseAsync_ConfiguresWalAndCreatesSchema()
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

        // Verify settlement_queue table exists
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='settlement_queue';";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.Equal(1, count);
        }
    }

    [Fact]
    public async Task UpsertDiscoveredSettlementsAsync_InsertsNewSettlementsAndPreservesExisting()
    {
        await _repository.InitializeDatabaseAsync();

        var initial = new List<DiscoveredSettlement>
        {
            new(101, "Settlement One"),
            new(102, "Settlement Two")
        };

        var inserted = await _repository.UpsertDiscoveredSettlementsAsync(initial);
        Assert.Equal(2, inserted);

        var totalCount = await _repository.GetCountAsync();
        Assert.Equal(2, totalCount);

        // Simulate item 101 moving to processing/completed
        using (var conn = _connectionFactory.CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE settlement_queue SET status = 'completed' WHERE settlement_id = 101;";
            await cmd.ExecuteNonQueryAsync();
        }

        // Upsert again with duplicate 101 and new item 103
        var nextBatch = new List<DiscoveredSettlement>
        {
            new(101, "Settlement One Renamed Alias"),
            new(103, "Settlement Three")
        };

        var newlyInserted = await _repository.UpsertDiscoveredSettlementsAsync(nextBatch);
        Assert.Equal(1, newlyInserted);

        // Check total count is now 3
        Assert.Equal(3, await _repository.GetCountAsync());

        // Check item 101 still has status 'completed'
        using (var conn = _connectionFactory.CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT status FROM settlement_queue WHERE settlement_id = 101;";
            var status = (string?)await cmd.ExecuteScalarAsync();
            Assert.Equal("completed", status);
        }
    }

    [Fact]
    public async Task ResetInFlightProcessingAsync_ResetsProcessingToPending()
    {
        await _repository.InitializeDatabaseAsync();

        var batch = new List<DiscoveredSettlement>
        {
            new(201, "Settlement 201"),
            new(202, "Settlement 202")
        };
        await _repository.UpsertDiscoveredSettlementsAsync(batch);

        using (var conn = _connectionFactory.CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE settlement_queue SET status = 'processing' WHERE settlement_id = 201;";
            await cmd.ExecuteNonQueryAsync();
        }

        var resetCount = await _repository.ResetInFlightProcessingAsync();
        Assert.Equal(1, resetCount);

        using (var conn = _connectionFactory.CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT status FROM settlement_queue WHERE settlement_id = 201;";
            var status = (string?)await cmd.ExecuteScalarAsync();
            Assert.Equal("pending", status);
        }
    }
}
