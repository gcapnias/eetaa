using System.Net;
using System.Text;

using Eetaa.Worker.Data;
using Eetaa.Worker.Discovery;
using Eetaa.Worker.Ingestion;
using Eetaa.Worker.Storage;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Eetaa.Worker.Tests.Ingestion;

public class SettlementIngestionServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteQueueRepository _queueRepo;
    private readonly SqliteSettlementDetailsRepository _detailsRepo;
    private readonly SettlementCsvParser _csvParser;

    public SettlementIngestionServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"eetaa_ingest_test_{Guid.NewGuid():N}.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);
        _queueRepo = new SqliteQueueRepository(_connectionFactory);
        _detailsRepo = new SqliteSettlementDetailsRepository(_connectionFactory);
        _csvParser = new SettlementCsvParser();
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    [Fact]
    public async Task IngestAsync_RecoversOrphanedProcessingRecordsOnStartup()
    {
        await _queueRepo.InitializeDatabaseAsync();
        await _queueRepo.UpsertDiscoveredSettlementsAsync(new[]
        {
            new DiscoveredSettlement(1, "Test 1"),
            new DiscoveredSettlement(2, "Test 2")
        });

        // Simulate crash: set both to 'processing'
        await _queueRepo.MarkProcessingAsync(1);
        await _queueRepo.MarkProcessingAsync(2);

        var processingBefore = await _queueRepo.GetCountByStatusAsync("processing");
        Assert.Equal(2, processingBefore);

        var mockHandler = new MockHttpMessageHandler(_ =>
        {
            var content = "\"ΟΝΟΜΑΣΙΑ ΟΙΚΙΣΜΟΥ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\n\"Test\";Αττικής;1980;;;01/01/1980;\"Ίδρυση\"\r\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "text/csv")
            };
        });

        var httpClient = new HttpClient(mockHandler);
        var channel = new SingleWriterChannel(100);
        var engine = new SingleWriterStorageEngine(channel, _detailsRepo);
        var service = new SettlementIngestionService(
            httpClient,
            _queueRepo,
            _csvParser,
            channel,
            new ExponentialBackoffRetryPolicy(1, 1),
            new IngestionOptions { Concurrency = 2, DelayMs = 0 },
            NullLogger<SettlementIngestionService>.Instance
        );

        // Run engine in background
        var engineTask = Task.Run(() => engine.ProcessChannelAsync());

        var result = await service.IngestAsync();
        channel.Complete();
        await engineTask;

        Assert.Equal(2, result.TotalProcessed);
        Assert.Equal(2, result.Succeeded);
        Assert.Equal(0, result.Failed);

        var processingAfter = await _queueRepo.GetCountByStatusAsync("processing");
        var completedAfter = await _queueRepo.GetCountByStatusAsync("completed");
        Assert.Equal(0, processingAfter);
        Assert.Equal(2, completedAfter);
    }

    [Fact]
    public async Task IngestAsync_RetriesTransientErrorsAndSucceeds()
    {
        await _queueRepo.InitializeDatabaseAsync();
        await _queueRepo.UpsertDiscoveredSettlementsAsync(new[]
        {
            new DiscoveredSettlement(10, "Retry Settlement")
        });

        var callCount = 0;
        var mockHandler = new MockHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount < 2)
            {
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
            }

            var content = "\"ΟΝΟΜΑΣΙΑ ΟΙΚΙΣΜΟΥ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\n\"Retry Settlement\";Αττικής;1990;;;01/01/1990;\"Ίδρυση\"\r\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "text/csv")
            };
        });

        var httpClient = new HttpClient(mockHandler);
        var channel = new SingleWriterChannel(10);
        var engine = new SingleWriterStorageEngine(channel, _detailsRepo);
        var service = new SettlementIngestionService(
            httpClient,
            _queueRepo,
            _csvParser,
            channel,
            new ExponentialBackoffRetryPolicy(3, 5),
            new IngestionOptions { Concurrency = 1, DelayMs = 0 },
            NullLogger<SettlementIngestionService>.Instance
        );

        var engineTask = Task.Run(() => engine.ProcessChannelAsync());
        var result = await service.IngestAsync();
        channel.Complete();
        await engineTask;

        Assert.Equal(1, result.TotalProcessed);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(2, callCount);

        var completedCount = await _queueRepo.GetCountByStatusAsync("completed");
        Assert.Equal(1, completedCount);
    }

    [Fact]
    public async Task IngestAsync_MarksFailedWhenRetriesExhausted()
    {
        await _queueRepo.InitializeDatabaseAsync();
        await _queueRepo.UpsertDiscoveredSettlementsAsync(new[]
        {
            new DiscoveredSettlement(99, "Failing Settlement")
        });

        var mockHandler = new MockHttpMessageHandler(_ =>
        {
            return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
        });

        var httpClient = new HttpClient(mockHandler);
        var channel = new SingleWriterChannel(10);
        var engine = new SingleWriterStorageEngine(channel, _detailsRepo);
        var service = new SettlementIngestionService(
            httpClient,
            _queueRepo,
            _csvParser,
            channel,
            new ExponentialBackoffRetryPolicy(2, 5),
            new IngestionOptions { Concurrency = 1, DelayMs = 0 },
            NullLogger<SettlementIngestionService>.Instance
        );

        var engineTask = Task.Run(() => engine.ProcessChannelAsync());
        var result = await service.IngestAsync();
        channel.Complete();
        await engineTask;

        Assert.Equal(1, result.TotalProcessed);
        Assert.Equal(0, result.Succeeded);
        Assert.Equal(1, result.Failed);

        var failedCount = await _queueRepo.GetCountByStatusAsync("failed");
        Assert.Equal(1, failedCount);
    }

    [Fact]
    public async Task IngestAsync_InitializesAndRecordsProgressOnCoordinator()
    {
        await _queueRepo.InitializeDatabaseAsync();
        await _queueRepo.UpsertDiscoveredSettlementsAsync(new[]
        {
            new DiscoveredSettlement(501, "Test 501"),
            new DiscoveredSettlement(502, "Test 502")
        });

        var mockHandler = new MockHttpMessageHandler(req =>
        {
            var uri = req.RequestUri?.ToString() ?? "";
            if (uri.Contains("sid=501"))
            {
                var content = "\"ΟΝΟΜΑΣΙΑ ΟΙΚΙΣΜΟΥ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\n\"Test 501\";Αττικής;1980;;;01/01/1980;\"Ίδρυση\"\r\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content, Encoding.UTF8, "text/csv")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(mockHandler);
        var channel = new SingleWriterChannel(10);
        var progressLogger = new TestLogger<IngestionProgressCoordinator>();
        using var coordinator = new IngestionProgressCoordinator(progressLogger);

        var service = new SettlementIngestionService(
            httpClient,
            _queueRepo,
            _csvParser,
            channel,
            new ExponentialBackoffRetryPolicy(1, 1),
            new IngestionOptions { Concurrency = 1, DelayMs = 0 },
            NullLogger<SettlementIngestionService>.Instance,
            coordinator
        );

        var result = await service.IngestAsync();

        Assert.Equal(2, coordinator.Total);
        Assert.Equal(1, coordinator.Downloaded);
    }
}