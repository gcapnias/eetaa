using Eetaa.Worker.Data;
using Eetaa.Worker.Discovery;
using Eetaa.Worker.Ingestion;
using Eetaa.Worker.Storage;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eetaa.Worker.Tests.Storage;

public class LocalAuthorityStorageEngineTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteLocalAuthorityQueueRepository _queueRepository;
    private readonly SqliteLocalAuthorityDetailsRepository _detailsRepository;
    private readonly LocalAuthorityChannel _channel;
    private readonly LocalAuthorityStorageEngine _storageEngine;

    public LocalAuthorityStorageEngineTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"eetaa_auth_engine_test_{Guid.NewGuid():N}.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);
        _queueRepository = new SqliteLocalAuthorityQueueRepository(_connectionFactory);
        _detailsRepository = new SqliteLocalAuthorityDetailsRepository(_connectionFactory);
        _channel = new LocalAuthorityChannel(100);
        _storageEngine = new LocalAuthorityStorageEngine(_channel, _detailsRepository, NullLogger<LocalAuthorityStorageEngine>.Instance);
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

        var seedList = Enumerable.Range(1, 20)
            .Select(i => new DiscoveredLocalAuthority(i, $"Δήμος {i}"))
            .ToList();
        await _queueRepository.UpsertDiscoveredAuthoritiesAsync(seedList);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var consumerTask = Task.Run(() => _storageEngine.ProcessChannelAsync(cts.Token));

        var producerTasks = Enumerable.Range(1, 20).Select(async i =>
        {
            var changes = new List<LocalAuthorityDetailRecord>
            {
                new(i, 1, AuthorityType.Municipality, $"Δήμος {i}", "Αττικής", 1920, "10", "A", "1920-01-01", "01/01/1920", $"Σύσταση {i}", null),
                new(i, 2, AuthorityType.Community, $"Κοινότητα {i}", "Αττικής", 1950, "50", "A", "1950-05-05", "05/05/1950", $"Μετατροπή {i}", null)
            };
            await _channel.EnqueueAsync(new LocalAuthorityPayload(i, changes), cts.Token);
        });

        await Task.WhenAll(producerTasks);
        _channel.Complete();
        await consumerTask;

        var totalChanges = await _detailsRepository.GetTotalChangesCountAsync();
        Assert.Equal(40, totalChanges);

        var completedCount = await _queueRepository.GetCountByStatusAsync("completed");
        Assert.Equal(20, completedCount);
    }

    [Fact]
    public async Task ProcessChannelAsync_HandlesMixedSuccessAndErrorPayloads()
    {
        await _queueRepository.InitializeDatabaseAsync();

        var seedList = new List<DiscoveredLocalAuthority>
        {
            new(101, "Valid 101"),
            new(102, "Error 102"),
            new(103, "Empty 103")
        };
        await _queueRepository.UpsertDiscoveredAuthoritiesAsync(seedList);

        var consumerTask = Task.Run(() => _storageEngine.ProcessChannelAsync());

        await _channel.EnqueueAsync(new LocalAuthorityPayload(101, new[]
        {
            new LocalAuthorityDetailRecord(101, 1, AuthorityType.Municipality, "Valid Name", null, null, null, null, null, null, "Desc", null)
        }));

        await _channel.EnqueueAsync(new LocalAuthorityPayload(102, Array.Empty<LocalAuthorityDetailRecord>(), "HTTP 502 Bad Gateway"));

        await _channel.EnqueueAsync(new LocalAuthorityPayload(103, Array.Empty<LocalAuthorityDetailRecord>()));

        _channel.Complete();
        await consumerTask;

        Assert.Equal(2, await _queueRepository.GetCountByStatusAsync("completed"));
        Assert.Equal(1, await _queueRepository.GetCountByStatusAsync("failed"));
        Assert.Equal(1, await _detailsRepository.GetTotalChangesCountAsync());
    }

    [Fact]
    public async Task ProcessChannelAsync_NotifiesProgressCoordinator_OnPersistenceAndErrors()
    {
        await _queueRepository.InitializeDatabaseAsync();
        var seedList = new List<DiscoveredLocalAuthority>
        {
            new(201, "Auth 201"),
            new(202, "Auth 202")
        };
        await _queueRepository.UpsertDiscoveredAuthoritiesAsync(seedList);

        var progressLogger = new Eetaa.Worker.Tests.Ingestion.TestLogger<IngestionProgressCoordinator>();
        using var coordinator = new IngestionProgressCoordinator(progressLogger, entityLabel: "local authority");
        coordinator.Start(2, () => _channel.Count);

        var engine = new LocalAuthorityStorageEngine(
            _channel,
            _detailsRepository,
            NullLogger<LocalAuthorityStorageEngine>.Instance,
            coordinator);

        var consumerTask = Task.Run(() => engine.ProcessChannelAsync());

        await _channel.EnqueueAsync(new LocalAuthorityPayload(201, new[]
        {
            new LocalAuthorityDetailRecord(201, 1, AuthorityType.Municipality, "Name 201", null, null, null, null, null, null, "Desc", null)
        }));
        await _channel.EnqueueAsync(new LocalAuthorityPayload(202, Array.Empty<LocalAuthorityDetailRecord>(), "Failed download"));

        _channel.Complete();
        await consumerTask;

        Assert.Equal(1, coordinator.Persisted);
        Assert.Equal(1, coordinator.Failed);
    }
}
