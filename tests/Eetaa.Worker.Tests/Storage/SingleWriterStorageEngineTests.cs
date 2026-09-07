using Eetaa.Worker.Data;
using Eetaa.Worker.Discovery;
using Eetaa.Worker.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eetaa.Worker.Tests.Storage;

public class SingleWriterStorageEngineTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteQueueRepository _queueRepository;
    private readonly SqliteSettlementDetailsRepository _detailsRepository;
    private readonly SingleWriterChannel _channel;
    private readonly SingleWriterStorageEngine _storageEngine;

    public SingleWriterStorageEngineTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"eetaa_engine_test_{Guid.NewGuid():N}.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);
        _queueRepository = new SqliteQueueRepository(_connectionFactory);
        _detailsRepository = new SqliteSettlementDetailsRepository(_connectionFactory);
        _channel = new SingleWriterChannel(100);
        _storageEngine = new SingleWriterStorageEngine(_channel, _detailsRepository, NullLogger<SingleWriterStorageEngine>.Instance);
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

    [Fact]
    public async Task ProcessChannelAsync_ConsumesConcurrentPayloadsAndExecutesAtomicWrites()
    {
        await _queueRepository.InitializeDatabaseAsync();

        // Seed 50 settlements
        var seedList = Enumerable.Range(1, 50)
            .Select(i => new DiscoveredSettlement(i, $"Settlement {i}"))
            .ToList();
        await _queueRepository.UpsertDiscoveredSettlementsAsync(seedList);

        // Start engine consumer in background task
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var consumerTask = Task.Run(() => _storageEngine.ProcessChannelAsync(cts.Token));

        // Produce 50 payloads concurrently from multiple producer tasks (simulating concurrent HTTP downloaders)
        var producerTasks = Enumerable.Range(1, 50).Select(async i =>
        {
            var changes = new List<SettlementDetailRecord>
            {
                new(i, 1, $"Name {i}", "Nomos", 1920, "10", "A", "1920-01-01", "01/01/1920", $"Desc {i}"),
                new(i, 2, $"Renamed {i}", "Nomos", 1950, "50", "A", "1950-05-05", "05/05/1950", $"Desc 2 {i}")
            };
            await _channel.EnqueueAsync(new SettlementPayload(i, changes), cts.Token);
        });

        await Task.WhenAll(producerTasks);

        // Complete channel
        _channel.Complete();

        // Wait for consumer to finish processing all messages
        await consumerTask;

        // Verify total details written = 100
        var totalChanges = await _detailsRepository.GetTotalChangesCountAsync();
        Assert.Equal(100, totalChanges);

        // Verify completed settlements = 50
        var completedCount = await _queueRepository.GetCountByStatusAsync("completed");
        Assert.Equal(50, completedCount);
    }

    [Fact]
    public async Task ProcessChannelAsync_HandlesMixedSuccessAndErrorPayloads()
    {
        await _queueRepository.InitializeDatabaseAsync();

        var seedList = new List<DiscoveredSettlement>
        {
            new(101, "Valid 101"),
            new(102, "Error 102"),
            new(103, "Empty 103")
        };
        await _queueRepository.UpsertDiscoveredSettlementsAsync(seedList);

        var consumerTask = Task.Run(() => _storageEngine.ProcessChannelAsync());

        // Enqueue valid
        await _channel.EnqueueAsync(new SettlementPayload(101, new[]
        {
            new SettlementDetailRecord(101, 1, "Valid Name", null, null, null, null, null, null, "Desc")
        }));

        // Enqueue error
        await _channel.EnqueueAsync(new SettlementPayload(102, Array.Empty<SettlementDetailRecord>(), "HTTP 404 Not Found"));

        // Enqueue empty
        await _channel.EnqueueAsync(new SettlementPayload(103, Array.Empty<SettlementDetailRecord>()));

        _channel.Complete();
        await consumerTask;

        Assert.Equal(2, await _queueRepository.GetCountByStatusAsync("completed"));
        Assert.Equal(1, await _queueRepository.GetCountByStatusAsync("failed"));
        Assert.Equal(1, await _detailsRepository.GetTotalChangesCountAsync());
    }
}
