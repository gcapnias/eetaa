using System.Net;
using System.Text;

using Eetaa.Worker.Data;
using Eetaa.Worker.Discovery;
using Eetaa.Worker.Ingestion;
using Eetaa.Worker.Storage;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Eetaa.Worker.Tests.Ingestion;

public class LocalAuthorityIngestionServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteLocalAuthorityQueueRepository _queueRepo;
    private readonly SqliteLocalAuthorityDetailsRepository _detailsRepo;
    private readonly LocalAuthorityCsvParser _csvParser;

    public LocalAuthorityIngestionServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"eetaa_la_ingest_test_{Guid.NewGuid():N}.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);
        _queueRepo = new SqliteLocalAuthorityQueueRepository(_connectionFactory);
        _detailsRepo = new SqliteLocalAuthorityDetailsRepository(_connectionFactory);
        _csvParser = new LocalAuthorityCsvParser();
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
        await _queueRepo.UpsertDiscoveredAuthoritiesAsync(new[]
        {
            new DiscoveredLocalAuthority(1, "Test 1"),
            new DiscoveredLocalAuthority(2, "Test 2")
        });

        // Simulate crash: set both to 'processing'
        await _queueRepo.MarkProcessingAsync(1);
        await _queueRepo.MarkProcessingAsync(2);

        var processingBefore = await _queueRepo.GetCountByStatusAsync("processing");
        Assert.Equal(2, processingBefore);

        var mockHandler = new MockHttpMessageHandler(_ =>
        {
            var content = "\"ΕΙΔΟΣ ΟΤΑ\";\"ΟΝΟΜΑΣΙΑ ΟΤΑ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\nΔ;\"Test\";Αττικής;1980;;;01/01/1980;\"Σύσταση\"\r\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "text/csv")
            };
        });

        var httpClient = new HttpClient(mockHandler);
        var channel = new LocalAuthorityChannel(100);
        var engine = new LocalAuthorityStorageEngine(channel, _detailsRepo);
        var service = new LocalAuthorityIngestionService(
            httpClient,
            _queueRepo,
            _csvParser,
            channel,
            new ExponentialBackoffRetryPolicy(1, 1),
            new LocalAuthorityIngestionOptions { Concurrency = 2, DelayMs = 0 },
            NullLogger<LocalAuthorityIngestionService>.Instance
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
        await _queueRepo.UpsertDiscoveredAuthoritiesAsync(new[]
        {
            new DiscoveredLocalAuthority(10, "Retry Authority")
        });

        var callCount = 0;
        var mockHandler = new MockHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount < 2)
            {
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
            }

            var content = "\"ΕΙΔΟΣ ΟΤΑ\";\"ΟΝΟΜΑΣΙΑ ΟΤΑ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\nΔ;\"Retry Authority\";Αττικής;1990;;;01/01/1990;\"Σύσταση\"\r\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "text/csv")
            };
        });

        var httpClient = new HttpClient(mockHandler);
        var channel = new LocalAuthorityChannel(10);
        var engine = new LocalAuthorityStorageEngine(channel, _detailsRepo);
        var service = new LocalAuthorityIngestionService(
            httpClient,
            _queueRepo,
            _csvParser,
            channel,
            new ExponentialBackoffRetryPolicy(3, 5),
            new LocalAuthorityIngestionOptions { Concurrency = 1, DelayMs = 0 },
            NullLogger<LocalAuthorityIngestionService>.Instance
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
        await _queueRepo.UpsertDiscoveredAuthoritiesAsync(new[]
        {
            new DiscoveredLocalAuthority(99, "Failing Authority")
        });

        var mockHandler = new MockHttpMessageHandler(_ =>
        {
            return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
        });

        var httpClient = new HttpClient(mockHandler);
        var channel = new LocalAuthorityChannel(10);
        var engine = new LocalAuthorityStorageEngine(channel, _detailsRepo);
        var service = new LocalAuthorityIngestionService(
            httpClient,
            _queueRepo,
            _csvParser,
            channel,
            new ExponentialBackoffRetryPolicy(2, 5),
            new LocalAuthorityIngestionOptions { Concurrency = 1, DelayMs = 0 },
            NullLogger<LocalAuthorityIngestionService>.Instance
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
        await _queueRepo.UpsertDiscoveredAuthoritiesAsync(new[]
        {
            new DiscoveredLocalAuthority(501, "Auth 501"),
            new DiscoveredLocalAuthority(502, "Auth 502")
        });

        var mockHandler = new MockHttpMessageHandler(req =>
        {
            var uri = req.RequestUri?.ToString() ?? "";
            if (uri.Contains("sid=501"))
            {
                var content = "\"ΕΙΔΟΣ ΟΤΑ\";\"ΟΝΟΜΑΣΙΑ ΟΤΑ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\nΔ;\"Auth 501\";Αττικής;1980;;;01/01/1980;\"Σύσταση\"\r\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content, Encoding.UTF8, "text/csv")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(mockHandler);
        var channel = new LocalAuthorityChannel(10);
        var progressLogger = new TestLogger<IngestionProgressCoordinator>();
        using var coordinator = new IngestionProgressCoordinator(progressLogger, entityLabel: "local authority");

        var service = new LocalAuthorityIngestionService(
            httpClient,
            _queueRepo,
            _csvParser,
            channel,
            new ExponentialBackoffRetryPolicy(1, 1),
            new LocalAuthorityIngestionOptions { Concurrency = 1, DelayMs = 0 },
            NullLogger<LocalAuthorityIngestionService>.Instance,
            coordinator
        );

        var result = await service.IngestAsync();

        Assert.Equal(2, coordinator.Total);
        Assert.Equal(1, coordinator.Downloaded);
    }
}
