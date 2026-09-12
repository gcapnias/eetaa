using System.Net;
using System.Text;

using Eetaa.Worker.Configuration;
using Eetaa.Worker.Data;
using Eetaa.Worker.Discovery;
using Eetaa.Worker.Ingestion;
using Eetaa.Worker.Storage;
using Eetaa.Worker.Tests.Ingestion;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Eetaa.Worker.Tests.Integration;

public class StorageConsumerLifecycleIntegrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteQueueRepository _queueRepo;
    private readonly SqliteSettlementDetailsRepository _detailsRepo;
    private readonly SettlementCsvParser _csvParser;

    public StorageConsumerLifecycleIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"eetaa_lifecycle_test_{Guid.NewGuid():N}.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);
        _queueRepo = new SqliteQueueRepository(_connectionFactory);
        _detailsRepo = new SqliteSettlementDetailsRepository(_connectionFactory);
        _csvParser = new SettlementCsvParser();
        Environment.ExitCode = 0;
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

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = (req, _) => Task.FromResult(handler(req));
        }

        public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }

    private class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _cts = new();
        public CancellationToken ApplicationStarted => _cts.Token;
        public CancellationToken ApplicationStopping => _cts.Token;
        public CancellationToken ApplicationStopped => _cts.Token;
        public void StopApplication() => _cts.Cancel();
    }

    private class FaultingStorageEngine : ISingleWriterStorageEngine
    {
        private readonly Exception _exceptionToThrow;
        private readonly TimeSpan _delay;

        public FaultingStorageEngine(Exception exceptionToThrow, TimeSpan? delay = null)
        {
            _exceptionToThrow = exceptionToThrow;
            _delay = delay ?? TimeSpan.FromMilliseconds(50);
        }

        public async Task ProcessChannelAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delay, cancellationToken);
            throw _exceptionToThrow;
        }
    }

    private class EarlyExitStorageEngine : ISingleWriterStorageEngine
    {
        public Task ProcessChannelAsync(CancellationToken cancellationToken = default)
        {
            // Consumer terminates immediately without reading anything
            return Task.CompletedTask;
        }
    }

    private class EarlyCancelingStorageEngine : ISingleWriterStorageEngine
    {
        private readonly TimeSpan _delay;

        public EarlyCancelingStorageEngine(TimeSpan? delay = null)
        {
            _delay = delay ?? TimeSpan.FromMilliseconds(50);
        }

        public async Task ProcessChannelAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delay, cancellationToken);
            throw new OperationCanceledException("Injected consumer cancellation");
        }
    }

    private class SlowDrainingStorageEngine : ISingleWriterStorageEngine
    {
        private readonly ISingleWriterChannel _channel;
        private readonly TimeSpan _itemDelay;

        public SlowDrainingStorageEngine(ISingleWriterChannel channel, TimeSpan itemDelay)
        {
            _channel = channel;
            _itemDelay = itemDelay;
        }

        public async Task ProcessChannelAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await foreach (var payload in _channel.Reader.ReadAllAsync(cancellationToken))
                {
                    await Task.Delay(_itemDelay, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Canceled by drain timeout
            }
        }
    }

    private record TestWorkerContext(
        Worker Worker,
        ISingleWriterChannel Channel,
        ISqliteQueueRepository QueueRepo,
        ISettlementDetailsRepository DetailsRepo,
        TestLogger<Worker> Logger,
        TestHostApplicationLifetime Lifetime
    );

    private TestWorkerContext CreateWorker(
        HttpMessageHandler httpHandler,
        Func<ISingleWriterChannel, ISingleWriterStorageEngine> storageEngineFactory,
        int channelCapacity = 100,
        int concurrency = 2,
        int delayMs = 0,
        TimeSpan? drainTimeout = null)
    {
        var httpClient = new HttpClient(httpHandler);
        var crawler = new IndexDiscoveryCrawler(
            httpClient,
            new IndexParser(),
            _queueRepo,
            new CrawlerOptions(),
            NullLogger<IndexDiscoveryCrawler>.Instance
        );

        var channel = new SingleWriterChannel(channelCapacity);
        var storageEngine = storageEngineFactory(channel);

        var ingestionService = new SettlementIngestionService(
            httpClient,
            _queueRepo,
            _csvParser,
            channel,
            new ExponentialBackoffRetryPolicy(1, 1),
            new IngestionOptions { Concurrency = concurrency, DelayMs = delayMs },
            NullLogger<SettlementIngestionService>.Instance
        );

        var cliOptions = new WorkerCliOptions
        {
            Mode = "download",
            DatabasePath = _dbPath
        };

        var workerLogger = new TestLogger<Worker>();
        var lifetime = new TestHostApplicationLifetime();
        var worker = new Worker(
            _queueRepo,
            crawler,
            ingestionService,
            channel,
            storageEngine,
            cliOptions,
            lifetime,
            workerLogger,
            drainTimeout: drainTimeout
        );

        return new TestWorkerContext(worker, channel, _queueRepo, _detailsRepo, workerLogger, lifetime);
    }

    [Fact]
    public async Task Worker_ConsumerFaultDuringIngestion_ImmediatelyAbortsProducerAndExitsWithError()
    {
        await _queueRepo.InitializeDatabaseAsync();
        var settlements = Enumerable.Range(1, 10)
            .Select(i => new DiscoveredSettlement(i, $"Settlement {i}"))
            .ToList();
        await _queueRepo.UpsertDiscoveredSettlementsAsync(settlements);

        var csv = "\"ΟΝΟΜΑΣΙΑ ΟΙΚΙΣΜΟΥ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\n\"Settlement\";Αττικής;1840;;;01/01/1840;\"Πρωτεύουσα\"\r\n";
        var mockHandler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(csv, Encoding.UTF8, "text/csv")
        });

        var context = CreateWorker(
            mockHandler,
            _ => new FaultingStorageEngine(new InvalidOperationException("Simulated storage crash")),
            channelCapacity: 1,
            concurrency: 2,
            delayMs: 50
        );

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await context.Worker.StartAsync(CancellationToken.None);

        var completedTask = await Task.WhenAny(context.Worker.ExecuteTask!, Task.Delay(2000));
        Assert.Same(context.Worker.ExecuteTask, completedTask);
        Assert.True(stopwatch.ElapsedMilliseconds < 1500, $"Expected abort under 1500ms but took {stopwatch.ElapsedMilliseconds}ms");

        // Worker must have exited with error
        Assert.Equal(1, Environment.ExitCode);
        Assert.Contains(context.Logger.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("Fatal error"));
    }

    [Fact]
    public async Task Worker_ConsumerEarlyExitDuringIngestion_ImmediatelyAbortsProducerAndExitsWithError()
    {
        await _queueRepo.InitializeDatabaseAsync();
        var settlements = Enumerable.Range(1, 10)
            .Select(i => new DiscoveredSettlement(i, $"Settlement {i}"))
            .ToList();
        await _queueRepo.UpsertDiscoveredSettlementsAsync(settlements);

        var csv = "\"ΟΝΟΜΑΣΙΑ ΟΙΚΙΣΜΟΥ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\n\"Settlement\";Αττικής;1840;;;01/01/1840;\"Πρωτεύουσα\"\r\n";
        var mockHandler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(csv, Encoding.UTF8, "text/csv")
        });

        var context = CreateWorker(
            mockHandler,
            _ => new EarlyExitStorageEngine(),
            channelCapacity: 1,
            concurrency: 2,
            delayMs: 50
        );

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await context.Worker.StartAsync(CancellationToken.None);

        var completedTask = await Task.WhenAny(context.Worker.ExecuteTask!, Task.Delay(2000));
        Assert.Same(context.Worker.ExecuteTask, completedTask);
        Assert.True(stopwatch.ElapsedMilliseconds < 1500, $"Expected abort under 1500ms but took {stopwatch.ElapsedMilliseconds}ms");

        // Worker must have exited with error
        Assert.Equal(1, Environment.ExitCode);
        Assert.Contains(context.Logger.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("Fatal error"));
    }

    [Fact]
    public async Task Worker_ConsumerEarlyCancellationDuringIngestion_ImmediatelyAbortsProducerAndExitsWithError()
    {
        await _queueRepo.InitializeDatabaseAsync();
        var settlements = Enumerable.Range(1, 10)
            .Select(i => new DiscoveredSettlement(i, $"Settlement {i}"))
            .ToList();
        await _queueRepo.UpsertDiscoveredSettlementsAsync(settlements);

        var csv = "\"ΟΝΟΜΑΣΙΑ ΟΙΚΙΣΜΟΥ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\n\"Settlement\";Αττικής;1840;;;01/01/1840;\"Πρωτεύουσα\"\r\n";
        var mockHandler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(csv, Encoding.UTF8, "text/csv")
        });

        var context = CreateWorker(
            mockHandler,
            _ => new EarlyCancelingStorageEngine(TimeSpan.FromMilliseconds(50)),
            channelCapacity: 1,
            concurrency: 2,
            delayMs: 50
        );

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await context.Worker.StartAsync(CancellationToken.None);

        var completedTask = await Task.WhenAny(context.Worker.ExecuteTask!, Task.Delay(2000));
        Assert.Same(context.Worker.ExecuteTask, completedTask);
        Assert.True(stopwatch.ElapsedMilliseconds < 1500, $"Expected abort under 1500ms but took {stopwatch.ElapsedMilliseconds}ms");

        // Worker must have exited with error
        Assert.Equal(1, Environment.ExitCode);
        Assert.Contains(context.Logger.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("Fatal error"));
    }

    [Fact]
    public async Task Worker_DrainsAllBufferedItems_BeforeCompletion()
    {
        await _queueRepo.InitializeDatabaseAsync();
        var settlements = Enumerable.Range(1, 10)
            .Select(i => new DiscoveredSettlement(i, $"Settlement {i}"))
            .ToList();
        await _queueRepo.UpsertDiscoveredSettlementsAsync(settlements);

        var csv = "\"ΟΝΟΜΑΣΙΑ ΟΙΚΙΣΜΟΥ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\n\"Settlement\";Αττικής;1840;;;01/01/1840;\"Πρωτεύουσα\"\r\n";
        var mockHandler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(csv, Encoding.UTF8, "text/csv")
        });

        var context = CreateWorker(
            mockHandler,
            ch => new SingleWriterStorageEngine(ch, _detailsRepo, NullLogger<SingleWriterStorageEngine>.Instance),
            channelCapacity: 100,
            concurrency: 4,
            delayMs: 0
        );

        Environment.ExitCode = 0;
        await context.Worker.StartAsync(CancellationToken.None);
        await context.Worker.ExecuteTask!;

        Assert.Equal(0, Environment.ExitCode);
        Assert.Equal(0, context.Channel.Count);
        Assert.Equal(10, await _queueRepo.GetCountByStatusAsync("completed"));
        Assert.Equal(10, await _detailsRepo.GetTotalChangesCountAsync());
        Assert.Contains(context.Logger.Entries, e => e.Message.Contains("All pending SQLite write transactions committed successfully."));
    }

    [Fact]
    public async Task Worker_IncompleteDrain_LogsErrorAndLeavesRecordsInProcessingState()
    {
        await _queueRepo.InitializeDatabaseAsync();
        var settlements = Enumerable.Range(1, 5)
            .Select(i => new DiscoveredSettlement(i, $"Settlement {i}"))
            .ToList();
        await _queueRepo.UpsertDiscoveredSettlementsAsync(settlements);

        var csv = "\"ΟΝΟΜΑΣΙΑ ΟΙΚΙΣΜΟΥ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\n\"Settlement\";Αττικής;1840;;;01/01/1840;\"Πρωτεύουσα\"\r\n";
        var mockHandler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(csv, Encoding.UTF8, "text/csv")
        });

        // Set a 50ms drain timeout, which is not enough for 5 items * 500ms
        var context = CreateWorker(
            mockHandler,
            ch => new SlowDrainingStorageEngine(ch, TimeSpan.FromMilliseconds(500)),
            channelCapacity: 100,
            concurrency: 5,
            delayMs: 0,
            drainTimeout: TimeSpan.FromMilliseconds(50)
        );

        await context.Worker.StartAsync(CancellationToken.None);
        await context.Worker.ExecuteTask!;

        // Error must be logged for incomplete drain
        Assert.Contains(context.Logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Channel drain timed out with"));

        // Items should remain in 'processing' status
        var processingCount = await _queueRepo.GetCountByStatusAsync("processing");
        Assert.True(processingCount > 0, "Expected orphaned 'processing' records remaining in queue");

        // Verify ResetInFlightProcessingAsync recovers them to 'pending'
        var resetCount = await _queueRepo.ResetInFlightProcessingAsync();
        Assert.Equal(processingCount, resetCount);
        Assert.Equal(0, await _queueRepo.GetCountByStatusAsync("processing"));
    }

    [Fact]
    public async Task Worker_IngestionRunningPast30Seconds_ContinuesPersistingRecordsWithoutPrematureCancellation()
    {
        await _queueRepo.InitializeDatabaseAsync();
        await _queueRepo.UpsertDiscoveredSettlementsAsync(new[]
        {
            new DiscoveredSettlement(1, "Settlement 1"),
            new DiscoveredSettlement(2, "Settlement 2")
        });

        var csv1 = "\"ΟΝΟΜΑΣΙΑ ΟΙΚΙΣΜΟΥ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\n\"Settlement 1\";Αττικής;1840;;;01/01/1840;\"Πρωτεύουσα\"\r\n";
        var csv2 = "\"ΟΝΟΜΑΣΙΑ ΟΙΚΙΣΜΟΥ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\n\"Settlement 2\";Αττικής;1845;;;01/01/1845;\"Πρωτεύουσα\"\r\n";

        var mockHandler = new MockHttpMessageHandler(async (req, ct) =>
        {
            var uri = req.RequestUri?.ToString() ?? string.Empty;
            if (uri.Contains("sid=1"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(csv1, Encoding.UTF8, "text/csv")
                };
            }
            if (uri.Contains("sid=2"))
            {
                // Delay past the 30-second threshold to verify consumer remains active
                await Task.Delay(31_000, ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(csv2, Encoding.UTF8, "text/csv")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var context = CreateWorker(
            mockHandler,
            ch => new SingleWriterStorageEngine(ch, _detailsRepo, NullLogger<SingleWriterStorageEngine>.Instance),
            channelCapacity: 100,
            concurrency: 1,
            delayMs: 0
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Environment.ExitCode = 0;
        await context.Worker.StartAsync(cts.Token);
        await context.Worker.ExecuteTask!;

        Assert.Equal(0, Environment.ExitCode);
        Assert.Equal(2, await _queueRepo.GetCountByStatusAsync("completed"));
        Assert.Equal(2, await _detailsRepo.GetTotalChangesCountAsync());
    }
}
