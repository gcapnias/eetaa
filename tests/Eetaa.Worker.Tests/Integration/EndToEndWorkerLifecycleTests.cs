using System.Net;
using System.Text;

using Eetaa.Worker.Configuration;
using Eetaa.Worker.Data;
using Eetaa.Worker.Discovery;
using Eetaa.Worker.Ingestion;
using Eetaa.Worker.Storage;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Eetaa.Worker.Tests.Integration;

public class EndToEndWorkerLifecycleTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteQueueRepository _queueRepo;
    private readonly SqliteSettlementDetailsRepository _detailsRepo;
    private readonly SettlementCsvParser _csvParser;

    public EndToEndWorkerLifecycleTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"eetaa_e2e_{Guid.NewGuid():N}.db");
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

    private class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _cts = new();
        public CancellationToken ApplicationStarted => _cts.Token;
        public CancellationToken ApplicationStopping => _cts.Token;
        public CancellationToken ApplicationStopped => _cts.Token;
        public void StopApplication() => _cts.Cancel();
    }

    [Fact]
    public async Task Worker_ExecutesAllMode_EndToEnd()
    {
        var htmlContent = @"
            <html><body>
            <table class='tbl_metaboles'>
                <tr><td>1</td><td><a href='oikmet_details.php?id=101'>Αθήνα (Αττικής)</a></td></tr>
                <tr><td>2</td><td><a href='oikmet_details.php?id=102'>Πειραιάς (Αττικής)</a></td></tr>
            </table>
            </body></html>
        ";

        var csv101 = "\"ΟΝΟΜΑΣΙΑ ΟΙΚΙΣΜΟΥ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\n\"Αθήνα\";Αττικής;1835;;;01/01/1835;\"Πρωτεύουσα\"\r\n";
        var csv102 = "\"ΟΝΟΜΑΣΙΑ ΟΙΚΙΣΜΟΥ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\n\"Πειραιάς\";Αττικής;1835;;;01/01/1835;\"Λιμήν\"\r\n";

        var mockHandler = new MockHttpMessageHandler(req =>
        {
            var uri = req.RequestUri?.ToString() ?? string.Empty;
            if (uri.Contains("oik_metaboles.php"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(htmlContent, Encoding.UTF8, "text/html")
                };
            }

            if (uri.Contains("sid=101"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(csv101, Encoding.UTF8, "text/csv")
                };
            }

            if (uri.Contains("sid=102"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(csv102, Encoding.UTF8, "text/csv")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(mockHandler);
        var crawler = new IndexDiscoveryCrawler(
            httpClient,
            new IndexParser(),
            _queueRepo,
            new CrawlerOptions { BaseUrl = "https://eetaa.gr/metaboles/oik_metaboles.php", StartPage = 1, MaxPages = 1 },
            NullLogger<IndexDiscoveryCrawler>.Instance
        );

        var channel = new SingleWriterChannel(100);
        var storageEngine = new SingleWriterStorageEngine(channel, _detailsRepo, NullLogger<SingleWriterStorageEngine>.Instance);
        var ingestionService = new SettlementIngestionService(
            httpClient,
            _queueRepo,
            _csvParser,
            channel,
            new ExponentialBackoffRetryPolicy(1, 1),
            new IngestionOptions { Concurrency = 2, DelayMs = 0 },
            NullLogger<SettlementIngestionService>.Instance
        );

        var cliOptions = new WorkerCliOptions
        {
            Mode = "all",
            DatabasePath = _dbPath
        };

        var worker = new Worker(
            _queueRepo,
            crawler,
            ingestionService,
            channel,
            storageEngine,
            cliOptions,
            new TestHostApplicationLifetime(),
            NullLogger<Worker>.Instance
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(cts.Token);
        await worker.ExecuteTask!;

        // Verify end-to-end outcome in SQLite
        var totalQueue = await _queueRepo.GetCountAsync();
        var completedQueue = await _queueRepo.GetCountByStatusAsync("completed");
        var totalDetails = await _detailsRepo.GetTotalChangesCountAsync();

        Assert.Equal(2, totalQueue);
        Assert.Equal(2, completedQueue);
        Assert.Equal(2, totalDetails);

        var changes101 = await _detailsRepo.GetChangesBySettlementIdAsync(101);
        Assert.Single(changes101);
        Assert.Equal("Αθήνα", changes101[0].SettlementName);
        Assert.Equal("1835-01-01", changes101[0].FekDate);
    }

    [Fact]
    public async Task Worker_ExecutesDiscoverModeOnly()
    {
        var htmlContent = @"
            <html><body>
            <table class='tbl_metaboles'>
                <tr><td>1</td><td><a href='oikmet_details.php?id=301'>Σέρρες (Σερρών)</a></td></tr>
            </table>
            </body></html>
        ";

        var mockHandler = new MockHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(htmlContent, Encoding.UTF8, "text/html")
            };
        });

        var httpClient = new HttpClient(mockHandler);
        var crawler = new IndexDiscoveryCrawler(
            httpClient,
            new IndexParser(),
            _queueRepo,
            new CrawlerOptions { BaseUrl = "https://eetaa.gr/metaboles/oik_metaboles.php", StartPage = 1, MaxPages = 1 },
            NullLogger<IndexDiscoveryCrawler>.Instance
        );

        var channel = new SingleWriterChannel(100);
        var storageEngine = new SingleWriterStorageEngine(channel, _detailsRepo);
        var ingestionService = new SettlementIngestionService(
            httpClient,
            _queueRepo,
            _csvParser,
            channel,
            new ExponentialBackoffRetryPolicy(1, 1),
            new IngestionOptions(),
            NullLogger<SettlementIngestionService>.Instance
        );

        var cliOptions = new WorkerCliOptions
        {
            Mode = "discover",
            DatabasePath = _dbPath
        };

        var worker = new Worker(
            _queueRepo,
            crawler,
            ingestionService,
            channel,
            storageEngine,
            cliOptions,
            new TestHostApplicationLifetime(),
            NullLogger<Worker>.Instance
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StartAsync(cts.Token);
        await worker.ExecuteTask!;

        var pendingCount = await _queueRepo.GetCountByStatusAsync("pending");
        var completedCount = await _queueRepo.GetCountByStatusAsync("completed");
        var detailsCount = await _detailsRepo.GetTotalChangesCountAsync();

        Assert.Equal(1, pendingCount);
        Assert.Equal(0, completedCount);
        Assert.Equal(0, detailsCount);
    }

    [Fact]
    public async Task Worker_ExecutesDownloadMode_EmitsProgressFeedback()
    {
        await _queueRepo.InitializeDatabaseAsync();
        await _queueRepo.UpsertDiscoveredSettlementsAsync(new[]
        {
            new DiscoveredSettlement(201, "Settlement 201"),
            new DiscoveredSettlement(202, "Settlement 202")
        });

        var csv201 = "\"ΟΝΟΜΑΣΙΑ ΟΙΚΙΣΜΟΥ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\n\"Settlement 201\";Αττικής;1840;;;01/01/1840;\"Πρωτεύουσα\"\r\n";
        var csv202 = "\"ΟΝΟΜΑΣΙΑ ΟΙΚΙΣΜΟΥ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\n\"Settlement 202\";Αττικής;1845;;;01/01/1845;\"Πρωτεύουσα\"\r\n";

        var mockHandler = new MockHttpMessageHandler(req =>
        {
            var uri = req.RequestUri?.ToString() ?? string.Empty;
            if (uri.Contains("sid=201"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(csv201, Encoding.UTF8, "text/csv")
                };
            }
            if (uri.Contains("sid=202"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(csv202, Encoding.UTF8, "text/csv")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(mockHandler);
        var crawler = new IndexDiscoveryCrawler(
            httpClient,
            new IndexParser(),
            _queueRepo,
            new CrawlerOptions(),
            NullLogger<IndexDiscoveryCrawler>.Instance
        );

        var channel = new SingleWriterChannel(100);
        var progressLogger = new Eetaa.Worker.Tests.Ingestion.TestLogger<IngestionProgressCoordinator>();
        using var coordinator = new IngestionProgressCoordinator(progressLogger);
        var storageEngine = new SingleWriterStorageEngine(channel, _detailsRepo, NullLogger<SingleWriterStorageEngine>.Instance, coordinator);
        var ingestionService = new SettlementIngestionService(
            httpClient,
            _queueRepo,
            _csvParser,
            channel,
            new ExponentialBackoffRetryPolicy(1, 1),
            new IngestionOptions { Concurrency = 2, DelayMs = 0 },
            NullLogger<SettlementIngestionService>.Instance,
            coordinator
        );

        var cliOptions = new WorkerCliOptions
        {
            Mode = "download",
            DatabasePath = _dbPath
        };

        var worker = new Worker(
            _queueRepo,
            crawler,
            ingestionService,
            channel,
            storageEngine,
            cliOptions,
            new TestHostApplicationLifetime(),
            NullLogger<Worker>.Instance,
            coordinator
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(cts.Token);
        await worker.ExecuteTask!;

        Assert.Equal(2, await _queueRepo.GetCountByStatusAsync("completed"));
        Assert.Equal(2, await _detailsRepo.GetTotalChangesCountAsync());

        // Verify progress coordinator logs
        Assert.NotEmpty(progressLogger.Entries);
        var finalLog = progressLogger.Entries.Last();
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, finalLog.Level);
        Assert.Contains("Progress: 2/2 settlements (100.0%)", finalLog.Message);
        Assert.Contains("Failed: 0", finalLog.Message);
    }
}