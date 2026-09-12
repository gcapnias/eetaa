using System.Net;
using System.Text;

using Eetaa.Worker.Configuration;
using Eetaa.Worker.Data;
using Eetaa.Worker.Discovery;
using Eetaa.Worker.Ingestion;
using Eetaa.Worker.Storage;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Eetaa.Worker.Tests.Integration;

public class EndToEndWorkerLifecycleTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteQueueRepository _queueRepo;
    private readonly SqliteLocalAuthorityQueueRepository _authorityQueueRepo;
    private readonly SqliteSettlementDetailsRepository _detailsRepo;
    private readonly SqliteLocalAuthorityDetailsRepository _authorityDetailsRepo;
    private readonly SettlementCsvParser _csvParser;
    private readonly LocalAuthorityCsvParser _authorityCsvParser;

    public EndToEndWorkerLifecycleTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"eetaa_e2e_{Guid.NewGuid():N}.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);
        _queueRepo = new SqliteQueueRepository(_connectionFactory);
        _authorityQueueRepo = new SqliteLocalAuthorityQueueRepository(_connectionFactory);
        _detailsRepo = new SqliteSettlementDetailsRepository(_connectionFactory);
        _authorityDetailsRepo = new SqliteLocalAuthorityDetailsRepository(_connectionFactory);
        _csvParser = new SettlementCsvParser();
        _authorityCsvParser = new LocalAuthorityCsvParser();
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

    [Fact]
    public async Task Worker_ExecutesLocalAuthorityDiscoverModeOnly()
    {
        var htmlContent = @"
            <html><body>
            <table class='tbl_metaboles'>
                <tr><td>1</td><td><a href='dkmet_details.php?id=501'>Δ. Θεσσαλονίκης (Θεσσαλονίκης)</a></td></tr>
                <tr><td>2</td><td><a href='dkmet_details.php?id=502'>Κ. Πανοράματος (Θεσσαλονίκης)</a></td></tr>
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
        var settlementCrawler = new IndexDiscoveryCrawler(
            httpClient,
            new IndexParser(),
            _queueRepo,
            new CrawlerOptions { BaseUrl = "https://eetaa.gr/metaboles/oik_metaboles.php", StartPage = 1, MaxPages = 1 },
            NullLogger<IndexDiscoveryCrawler>.Instance
        );
        var authorityCrawler = new LocalAuthorityDiscoveryCrawler(
            httpClient,
            new IndexParser(),
            _authorityQueueRepo,
            new LocalAuthorityCrawlerOptions { BaseUrl = "https://eetaa.gr/metaboles/dk_metaboles.php", StartPage = 1, MaxPages = 1 },
            NullLogger<LocalAuthorityDiscoveryCrawler>.Instance
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
            Entity = "authorities",
            Mode = "discover",
            DatabasePath = _dbPath
        };

        var worker = new Worker(
            _queueRepo,
            settlementCrawler,
            ingestionService,
            channel,
            storageEngine,
            cliOptions,
            new TestHostApplicationLifetime(),
            NullLogger<Worker>.Instance,
            authorityRepository: _authorityQueueRepo,
            authorityCrawler: authorityCrawler
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StartAsync(cts.Token);
        await worker.ExecuteTask!;

        var authorityPending = await _authorityQueueRepo.GetCountByStatusAsync("pending");
        var settlementPending = await _queueRepo.GetCountByStatusAsync("pending");

        Assert.Equal(2, authorityPending);
        Assert.Equal(0, settlementPending);
    }

    [Fact]
    public async Task Worker_ExecutesLocalAuthorityDownloadModeOnly()
    {
        await _authorityQueueRepo.InitializeDatabaseAsync();
        await _authorityQueueRepo.UpsertDiscoveredAuthoritiesAsync(new[]
        {
            new DiscoveredLocalAuthority(601, "Δ. Αθηναίων"),
            new DiscoveredLocalAuthority(602, "Κ. Κηφισιάς")
        });

        var csv601 = "\"ΕΙΔΟΣ ΟΤΑ\";\"ΟΝΟΜΑΣΙΑ ΟΤΑ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\nΔ;\"Δ. Αθηναίων\";Αττικής;1835;;;01/01/1835;\"Σύσταση δήμου\"\r\n";
        var csv602 = "\"ΕΙΔΟΣ ΟΤΑ\";\"ΟΝΟΜΑΣΙΑ ΟΤΑ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\nΚ;\"Κ. Κηφισιάς\";Αττικής;1925;;;05/05/1925;\"Σύσταση κοινότητας\"\r\n";

        var mockHandler = new MockHttpMessageHandler(req =>
        {
            var uri = req.RequestUri?.ToString() ?? string.Empty;
            if (uri.Contains("sid=601"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(csv601, Encoding.UTF8, "text/csv")
                };
            }
            if (uri.Contains("sid=602"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(csv602, Encoding.UTF8, "text/csv")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(mockHandler);
        var settlementCrawler = new IndexDiscoveryCrawler(
            httpClient,
            new IndexParser(),
            _queueRepo,
            new CrawlerOptions(),
            NullLogger<IndexDiscoveryCrawler>.Instance
        );

        var authorityChannel = new LocalAuthorityChannel(100);
        var progressLogger = new Eetaa.Worker.Tests.Ingestion.TestLogger<IngestionProgressCoordinator>();
        using var coordinator = new IngestionProgressCoordinator(progressLogger, entityLabel: "local authority");
        var authorityStorageEngine = new LocalAuthorityStorageEngine(authorityChannel, _authorityDetailsRepo, NullLogger<LocalAuthorityStorageEngine>.Instance, coordinator);
        var authorityIngestionService = new LocalAuthorityIngestionService(
            httpClient,
            _authorityQueueRepo,
            _authorityCsvParser,
            authorityChannel,
            new ExponentialBackoffRetryPolicy(1, 1),
            new LocalAuthorityIngestionOptions { Concurrency = 2, DelayMs = 0 },
            NullLogger<LocalAuthorityIngestionService>.Instance,
            coordinator
        );

        var cliOptions = new WorkerCliOptions
        {
            Entity = "authorities",
            Mode = "download",
            DatabasePath = _dbPath
        };

        var worker = new Worker(
            _queueRepo,
            settlementCrawler,
            null,
            null,
            null,
            cliOptions,
            new TestHostApplicationLifetime(),
            NullLogger<Worker>.Instance,
            authorityRepository: _authorityQueueRepo,
            authorityIngestionService: authorityIngestionService,
            authorityChannel: authorityChannel,
            authorityStorageEngine: authorityStorageEngine,
            authorityProgressCoordinator: coordinator
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(cts.Token);
        await worker.ExecuteTask!;

        Assert.Equal(2, await _authorityQueueRepo.GetCountByStatusAsync("completed"));
        Assert.Equal(2, await _authorityDetailsRepo.GetTotalChangesCountAsync());

        var changes601 = await _authorityDetailsRepo.GetChangesByAuthorityIdAsync(601);
        Assert.Single(changes601);
        Assert.Equal("Δ. Αθηναίων", changes601[0].AuthorityName);
        Assert.Equal("1835-01-01", changes601[0].FekDate);
        Assert.Equal(AuthorityType.Municipality, changes601[0].AuthorityType);

        var changes602 = await _authorityDetailsRepo.GetChangesByAuthorityIdAsync(602);
        Assert.Single(changes602);
        Assert.Equal("Κ. Κηφισιάς", changes602[0].AuthorityName);
        Assert.Equal(AuthorityType.Community, changes602[0].AuthorityType);

        // Verify entity-labeled progress coordinator output
        Assert.NotEmpty(progressLogger.Entries);
        var finalLog = progressLogger.Entries.Last();
        Assert.Contains("local authorities", finalLog.Message);
    }

    [Fact]
    public async Task Worker_ExecutesAllEntitiesSequentially_WithoutChannelClosedException()
    {
        await _queueRepo.InitializeDatabaseAsync();
        await _authorityQueueRepo.InitializeDatabaseAsync();

        await _queueRepo.UpsertDiscoveredSettlementsAsync(new[]
        {
            new DiscoveredSettlement(701, "Settlement 701")
        });
        await _authorityQueueRepo.UpsertDiscoveredAuthoritiesAsync(new[]
        {
            new DiscoveredLocalAuthority(801, "Authority 801")
        });

        var settlementCsv = "\"ΟΝΟΜΑΣΙΑ ΟΙΚΙΣΜΟΥ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\n\"Settlement 701\";Αττικής;1850;;;01/01/1850;\"Πρωτεύουσα\"\r\n";
        var authorityCsv = "\"ΕΙΔΟΣ ΟΤΑ\";\"ΟΝΟΜΑΣΙΑ ΟΤΑ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\nΔ;\"Authority 801\";Αττικής;1850;;;01/01/1850;\"Σύσταση\"\r\n";

        var mockHandler = new MockHttpMessageHandler(req =>
        {
            var uri = req.RequestUri?.ToString() ?? string.Empty;
            if (uri.Contains("sid=701"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(settlementCsv, Encoding.UTF8, "text/csv")
                };
            }
            if (uri.Contains("sid=801"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(authorityCsv, Encoding.UTF8, "text/csv")
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

        var settlementChannel = new SingleWriterChannel(100);
        var settlementStorage = new SingleWriterStorageEngine(settlementChannel, _detailsRepo);
        var settlementIngestion = new SettlementIngestionService(
            httpClient,
            _queueRepo,
            _csvParser,
            settlementChannel,
            new ExponentialBackoffRetryPolicy(1, 1),
            new IngestionOptions { Concurrency = 1, DelayMs = 0 },
            NullLogger<SettlementIngestionService>.Instance
        );

        var authorityChannel = new LocalAuthorityChannel(100);
        var authorityStorage = new LocalAuthorityStorageEngine(authorityChannel, _authorityDetailsRepo);
        var authorityIngestion = new LocalAuthorityIngestionService(
            httpClient,
            _authorityQueueRepo,
            _authorityCsvParser,
            authorityChannel,
            new ExponentialBackoffRetryPolicy(1, 1),
            new LocalAuthorityIngestionOptions { Concurrency = 1, DelayMs = 0 },
            NullLogger<LocalAuthorityIngestionService>.Instance
        );

        var cliOptions = new WorkerCliOptions
        {
            Entity = "all",
            Mode = "download",
            DatabasePath = _dbPath
        };

        var worker = new Worker(
            _queueRepo,
            crawler,
            settlementIngestion,
            settlementChannel,
            settlementStorage,
            cliOptions,
            new TestHostApplicationLifetime(),
            NullLogger<Worker>.Instance,
            authorityRepository: _authorityQueueRepo,
            authorityIngestionService: authorityIngestion,
            authorityChannel: authorityChannel,
            authorityStorageEngine: authorityStorage
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(cts.Token);
        await worker.ExecuteTask!;

        Assert.Equal(1, await _queueRepo.GetCountByStatusAsync("completed"));
        Assert.Equal(1, await _detailsRepo.GetTotalChangesCountAsync());

        Assert.Equal(1, await _authorityQueueRepo.GetCountByStatusAsync("completed"));
        Assert.Equal(1, await _authorityDetailsRepo.GetTotalChangesCountAsync());
    }

    [Fact]
    public void GenericHost_BuildAndValidateServiceProvider_ResolvesWorkerHostedServiceSuccessfully()
    {
        var services = new ServiceCollection();

        var cliOptions = new WorkerCliOptions { DatabasePath = _dbPath, Mode = "download", Entity = "all" };
        services.AddSingleton(cliOptions);

        services.AddSingleton<ISqliteConnectionFactory>(_ => new SqliteConnectionFactory(_dbPath));
        services.AddSingleton<ISqliteQueueRepository, SqliteQueueRepository>();
        services.AddSingleton<ISqliteLocalAuthorityQueueRepository, SqliteLocalAuthorityQueueRepository>();
        services.AddSingleton<ISettlementDetailsRepository, SqliteSettlementDetailsRepository>();
        services.AddSingleton<ILocalAuthorityDetailsRepository, SqliteLocalAuthorityDetailsRepository>();

        services.AddSingleton<ISettlementCsvParser, SettlementCsvParser>();
        services.AddSingleton<ILocalAuthorityCsvParser, LocalAuthorityCsvParser>();
        services.AddScoped<ISingleWriterChannel, SingleWriterChannel>();
        services.AddScoped<ISingleWriterStorageEngine, SingleWriterStorageEngine>();

        services.AddScoped<ILocalAuthorityChannel, LocalAuthorityChannel>();
        services.AddScoped<ISingleWriterChannel<LocalAuthorityPayload>>(sp => sp.GetRequiredService<ILocalAuthorityChannel>());
        services.AddScoped<ILocalAuthorityStorageEngine, LocalAuthorityStorageEngine>();

        services.AddHttpClient<IIndexDiscoveryCrawler, IndexDiscoveryCrawler>();
        services.AddHttpClient<ILocalAuthorityDiscoveryCrawler, LocalAuthorityDiscoveryCrawler>();
        services.AddSingleton<IndexParser>();
        services.AddSingleton(new CrawlerOptions());
        services.AddSingleton(new LocalAuthorityCrawlerOptions());

        services.AddHttpClient("Settlements");
        services.AddHttpClient("Authorities");
        services.AddSingleton<IRetryPolicy, ExponentialBackoffRetryPolicy>();
        services.AddSingleton(new IngestionOptions());
        services.AddSingleton(new LocalAuthorityIngestionOptions());

        services.AddSingleton<IHostApplicationLifetime, TestHostApplicationLifetime>();
        services.AddLogging();

        services.AddHostedService<Worker>();

        var providerOptions = new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        };

        var sp = services.BuildServiceProvider(providerOptions);
        var hostedServices = sp.GetServices<IHostedService>();
        Assert.Contains(hostedServices, s => s is Worker);
    }

    [Fact]
    public async Task Worker_GenericHostScopeFactory_ExecutesAllEntitiesSequentially()
    {
        await _queueRepo.InitializeDatabaseAsync();
        await _authorityQueueRepo.InitializeDatabaseAsync();

        await _queueRepo.UpsertDiscoveredSettlementsAsync(new[]
        {
            new DiscoveredSettlement(701, "Settlement 701")
        });
        await _authorityQueueRepo.UpsertDiscoveredAuthoritiesAsync(new[]
        {
            new DiscoveredLocalAuthority(801, "Authority 801")
        });

        var settlementCsv = "\"ΟΝΟΜΑΣΙΑ ΟΙΚΙΣΜΟΥ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\n\"Settlement 701\";Αττικής;1850;;;01/01/1850;\"Πρωτεύουσα\"\r\n";
        var authorityCsv = "\"ΕΙΔΟΣ ΟΤΑ\";\"ΟΝΟΜΑΣΙΑ ΟΤΑ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\nΔ;\"Authority 801\";Αττικής;1850;;;01/01/1850;\"Σύσταση\"\r\n";

        var mockHandler = new MockHttpMessageHandler(req =>
        {
            var uri = req.RequestUri?.ToString() ?? string.Empty;
            if (uri.Contains("sid=701"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(settlementCsv, Encoding.UTF8, "text/csv")
                };
            }
            if (uri.Contains("sid=801"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(authorityCsv, Encoding.UTF8, "text/csv")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var services = new ServiceCollection();

        var cliOptions = new WorkerCliOptions
        {
            DatabasePath = _dbPath,
            Mode = "download",
            Entity = "all"
        };
        services.AddSingleton(cliOptions);

        services.AddSingleton<ISqliteConnectionFactory>(_ => new SqliteConnectionFactory(_dbPath));
        services.AddSingleton<ISqliteQueueRepository>(_queueRepo);
        services.AddSingleton<ISqliteLocalAuthorityQueueRepository>(_authorityQueueRepo);
        services.AddSingleton<ISettlementDetailsRepository>(_detailsRepo);
        services.AddSingleton<ILocalAuthorityDetailsRepository>(_authorityDetailsRepo);

        services.AddSingleton<ISettlementCsvParser, SettlementCsvParser>();
        services.AddSingleton<ILocalAuthorityCsvParser, LocalAuthorityCsvParser>();
        services.AddScoped<ISingleWriterChannel, SingleWriterChannel>();
        services.AddScoped<ILocalAuthorityChannel, LocalAuthorityChannel>();

        services.AddHttpClient<IIndexDiscoveryCrawler, IndexDiscoveryCrawler>();
        services.AddHttpClient<ILocalAuthorityDiscoveryCrawler, LocalAuthorityDiscoveryCrawler>();
        services.AddSingleton<IndexParser>();
        services.AddSingleton(new CrawlerOptions());
        services.AddSingleton(new LocalAuthorityCrawlerOptions());

        services.AddHttpClient("Settlements").ConfigurePrimaryHttpMessageHandler(() => mockHandler);
        services.AddHttpClient("Authorities").ConfigurePrimaryHttpMessageHandler(() => mockHandler);
        services.AddSingleton<IRetryPolicy, ExponentialBackoffRetryPolicy>();
        services.AddSingleton(new IngestionOptions { Concurrency = 1, DelayMs = 0 });
        services.AddSingleton(new LocalAuthorityIngestionOptions { Concurrency = 1, DelayMs = 0 });

        services.AddSingleton<IHostApplicationLifetime, TestHostApplicationLifetime>();
        services.AddLogging();
        services.AddHostedService<Worker>();

        var sp = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        var worker = (Worker)sp.GetServices<IHostedService>().First(s => s is Worker);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(cts.Token);
        await worker.ExecuteTask!;

        Assert.Equal(1, await _queueRepo.GetCountByStatusAsync("completed"));
        Assert.Equal(1, await _detailsRepo.GetTotalChangesCountAsync());

        Assert.Equal(1, await _authorityQueueRepo.GetCountByStatusAsync("completed"));
        Assert.Equal(1, await _authorityDetailsRepo.GetTotalChangesCountAsync());
    }

    [Fact]
    public async Task Worker_ExecutesAllEntityAndAllMode_SequentialPipeline_PopulatesBothTables()
    {
        var settlementHtml = @"
            <html><body>
            <table class='tbl_metaboles'>
                <tr><td>1</td><td><a href='oikmet_details.php?id=901'>Χαλάνδρι (Αττικής)</a></td></tr>
            </table>
            </body></html>
        ";
        var authorityHtml = @"
            <html><body>
            <table class='tbl_metaboles'>
                <tr><td>1</td><td><a href='dkmet_details.php?id=951'>Δ. Χαλανδρίου (Αττικής)</a></td></tr>
            </table>
            </body></html>
        ";

        var settlementCsv = "\"ΟΝΟΜΑΣΙΑ ΟΙΚΙΣΜΟΥ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\n\"Χαλάνδρι\";Αττικής;1925;;;01/01/1925;\"Προσάρτηση\"\r\n";
        var authorityCsv = "\"ΕΙΔΟΣ ΟΤΑ\";\"ΟΝΟΜΑΣΙΑ ΟΤΑ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\nΔ;\"Δ. Χαλανδρίου\";Αττικής;1944;;;01/01/1944;\"Σύσταση δήμου\"\r\n";

        var mockHandler = new MockHttpMessageHandler(req =>
        {
            var uri = req.RequestUri?.ToString() ?? string.Empty;
            if (uri.Contains("oik_metaboles.php"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(settlementHtml, Encoding.UTF8, "text/html")
                };
            }
            if (uri.Contains("dk_metaboles.php"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(authorityHtml, Encoding.UTF8, "text/html")
                };
            }
            if (uri.Contains("sid=901"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(settlementCsv, Encoding.UTF8, "text/csv")
                };
            }
            if (uri.Contains("sid=951"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(authorityCsv, Encoding.UTF8, "text/csv")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var services = new ServiceCollection();
        var cliOptions = new WorkerCliOptions
        {
            DatabasePath = _dbPath,
            Mode = "all",
            Entity = "all"
        };
        services.AddSingleton(cliOptions);

        services.AddSingleton<ISqliteConnectionFactory>(_ => new SqliteConnectionFactory(_dbPath));
        services.AddSingleton<ISqliteQueueRepository>(_queueRepo);
        services.AddSingleton<ISqliteLocalAuthorityQueueRepository>(_authorityQueueRepo);
        services.AddSingleton<ISettlementDetailsRepository>(_detailsRepo);
        services.AddSingleton<ILocalAuthorityDetailsRepository>(_authorityDetailsRepo);

        services.AddSingleton<ISettlementCsvParser, SettlementCsvParser>();
        services.AddSingleton<ILocalAuthorityCsvParser, LocalAuthorityCsvParser>();
        services.AddScoped<ISingleWriterChannel, SingleWriterChannel>();
        services.AddScoped<ILocalAuthorityChannel, LocalAuthorityChannel>();

        services.AddHttpClient<IIndexDiscoveryCrawler, IndexDiscoveryCrawler>()
            .ConfigurePrimaryHttpMessageHandler(() => mockHandler);
        services.AddHttpClient<ILocalAuthorityDiscoveryCrawler, LocalAuthorityDiscoveryCrawler>()
            .ConfigurePrimaryHttpMessageHandler(() => mockHandler);
        services.AddSingleton<IndexParser>();
        services.AddSingleton(new CrawlerOptions { BaseUrl = "https://eetaa.gr/metaboles/oik_metaboles.php", StartPage = 1, MaxPages = 1 });
        services.AddSingleton(new LocalAuthorityCrawlerOptions { BaseUrl = "https://eetaa.gr/metaboles/dk_metaboles.php", StartPage = 1, MaxPages = 1 });

        services.AddHttpClient("Settlements").ConfigurePrimaryHttpMessageHandler(() => mockHandler);
        services.AddHttpClient("Authorities").ConfigurePrimaryHttpMessageHandler(() => mockHandler);
        services.AddSingleton<IRetryPolicy, ExponentialBackoffRetryPolicy>();
        services.AddSingleton(new IngestionOptions { Concurrency = 1, DelayMs = 0 });
        services.AddSingleton(new LocalAuthorityIngestionOptions { Concurrency = 1, DelayMs = 0 });

        services.AddSingleton<IHostApplicationLifetime, TestHostApplicationLifetime>();
        services.AddLogging();
        services.AddHostedService<Worker>();

        var sp = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        var worker = (Worker)sp.GetServices<IHostedService>().First(s => s is Worker);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(cts.Token);
        await worker.ExecuteTask!;

        // 1. Settlement Discovery & Ingestion verified
        Assert.Equal(1, await _queueRepo.GetCountAsync());
        Assert.Equal(1, await _queueRepo.GetCountByStatusAsync("completed"));
        Assert.Equal(1, await _detailsRepo.GetTotalChangesCountAsync());
        var settlementChanges = await _detailsRepo.GetChangesBySettlementIdAsync(901);
        Assert.Single(settlementChanges);
        Assert.Equal("Χαλάνδρι", settlementChanges[0].SettlementName);

        // 2. Local Authority Discovery & Ingestion verified
        Assert.Equal(1, await _authorityQueueRepo.GetCountAsync());
        Assert.Equal(1, await _authorityQueueRepo.GetCountByStatusAsync("completed"));
        Assert.Equal(1, await _authorityDetailsRepo.GetTotalChangesCountAsync());
        var authorityChanges = await _authorityDetailsRepo.GetChangesByAuthorityIdAsync(951);
        Assert.Single(authorityChanges);
        Assert.Equal("Δ. Χαλανδρίου", authorityChanges[0].AuthorityName);
        Assert.Equal(AuthorityType.Municipality, authorityChanges[0].AuthorityType);
    }

    [Fact]
    public async Task Worker_ExecutesAllEntityAndDiscoverMode_DiscoversBothQueuesWithoutIngesting()
    {
        var settlementHtml = @"
            <html><body>
            <table class='tbl_metaboles'>
                <tr><td>1</td><td><a href='oikmet_details.php?id=911'>Μαρούσι (Αττικής)</a></td></tr>
            </table>
            </body></html>
        ";
        var authorityHtml = @"
            <html><body>
            <table class='tbl_metaboles'>
                <tr><td>1</td><td><a href='dkmet_details.php?id=961'>Δ. Αμαρουσίου (Αττικής)</a></td></tr>
            </table>
            </body></html>
        ";

        var mockHandler = new MockHttpMessageHandler(req =>
        {
            var uri = req.RequestUri?.ToString() ?? string.Empty;
            if (uri.Contains("oik_metaboles.php"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(settlementHtml, Encoding.UTF8, "text/html")
                };
            }
            if (uri.Contains("dk_metaboles.php"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(authorityHtml, Encoding.UTF8, "text/html")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(mockHandler);
        var settlementCrawler = new IndexDiscoveryCrawler(
            httpClient,
            new IndexParser(),
            _queueRepo,
            new CrawlerOptions { BaseUrl = "https://eetaa.gr/metaboles/oik_metaboles.php", StartPage = 1, MaxPages = 1 },
            NullLogger<IndexDiscoveryCrawler>.Instance
        );
        var authorityCrawler = new LocalAuthorityDiscoveryCrawler(
            httpClient,
            new IndexParser(),
            _authorityQueueRepo,
            new LocalAuthorityCrawlerOptions { BaseUrl = "https://eetaa.gr/metaboles/dk_metaboles.php", StartPage = 1, MaxPages = 1 },
            NullLogger<LocalAuthorityDiscoveryCrawler>.Instance
        );

        var cliOptions = new WorkerCliOptions
        {
            Entity = "all",
            Mode = "discover",
            DatabasePath = _dbPath
        };

        var worker = new Worker(
            _queueRepo,
            settlementCrawler,
            null,
            null,
            null,
            cliOptions,
            new TestHostApplicationLifetime(),
            NullLogger<Worker>.Instance,
            authorityRepository: _authorityQueueRepo,
            authorityCrawler: authorityCrawler
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StartAsync(cts.Token);
        await worker.ExecuteTask!;

        Assert.Equal(1, await _queueRepo.GetCountByStatusAsync("pending"));
        Assert.Equal(0, await _queueRepo.GetCountByStatusAsync("completed"));
        Assert.Equal(0, await _detailsRepo.GetTotalChangesCountAsync());

        Assert.Equal(1, await _authorityQueueRepo.GetCountByStatusAsync("pending"));
        Assert.Equal(0, await _authorityQueueRepo.GetCountByStatusAsync("completed"));
        Assert.Equal(0, await _authorityDetailsRepo.GetTotalChangesCountAsync());
    }

    [Fact]
    public async Task Worker_ExecutesAuthoritiesAllMode_EndToEnd()
    {
        var authorityHtml = @"
            <html><body>
            <table class='tbl_metaboles'>
                <tr><td>1</td><td><a href='dkmet_details.php?id=971'>Δ. Πατρέων (Αχαΐας)</a></td></tr>
            </table>
            </body></html>
        ";
        var authorityCsv = "\"ΕΙΔΟΣ ΟΤΑ\";\"ΟΝΟΜΑΣΙΑ ΟΤΑ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\nΔ;\"Δ. Πατρέων\";Αχαΐας;1835;;;01/01/1835;\"Σύσταση δήμου\"\r\n";

        var mockHandler = new MockHttpMessageHandler(req =>
        {
            var uri = req.RequestUri?.ToString() ?? string.Empty;
            if (uri.Contains("dk_metaboles.php"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(authorityHtml, Encoding.UTF8, "text/html")
                };
            }
            if (uri.Contains("sid=971"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(authorityCsv, Encoding.UTF8, "text/csv")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(mockHandler);
        var authorityCrawler = new LocalAuthorityDiscoveryCrawler(
            httpClient,
            new IndexParser(),
            _authorityQueueRepo,
            new LocalAuthorityCrawlerOptions { BaseUrl = "https://eetaa.gr/metaboles/dk_metaboles.php", StartPage = 1, MaxPages = 1 },
            NullLogger<LocalAuthorityDiscoveryCrawler>.Instance
        );

        var authorityChannel = new LocalAuthorityChannel(100);
        var authorityStorage = new LocalAuthorityStorageEngine(authorityChannel, _authorityDetailsRepo);
        var authorityIngestion = new LocalAuthorityIngestionService(
            httpClient,
            _authorityQueueRepo,
            _authorityCsvParser,
            authorityChannel,
            new ExponentialBackoffRetryPolicy(1, 1),
            new LocalAuthorityIngestionOptions { Concurrency = 1, DelayMs = 0 },
            NullLogger<LocalAuthorityIngestionService>.Instance
        );

        var settlementCrawler = new IndexDiscoveryCrawler(
            httpClient,
            new IndexParser(),
            _queueRepo,
            new CrawlerOptions(),
            NullLogger<IndexDiscoveryCrawler>.Instance
        );

        var cliOptions = new WorkerCliOptions
        {
            Entity = "authorities",
            Mode = "all",
            DatabasePath = _dbPath
        };

        var worker = new Worker(
            _queueRepo,
            settlementCrawler,
            null,
            null,
            null,
            cliOptions,
            new TestHostApplicationLifetime(),
            NullLogger<Worker>.Instance,
            authorityRepository: _authorityQueueRepo,
            authorityCrawler: authorityCrawler,
            authorityIngestionService: authorityIngestion,
            authorityChannel: authorityChannel,
            authorityStorageEngine: authorityStorage
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(cts.Token);
        await worker.ExecuteTask!;

        // Authorities should be discovered and ingested
        Assert.Equal(1, await _authorityQueueRepo.GetCountAsync());
        Assert.Equal(1, await _authorityQueueRepo.GetCountByStatusAsync("completed"));
        Assert.Equal(1, await _authorityDetailsRepo.GetTotalChangesCountAsync());

        // Settlements should not be touched
        Assert.Equal(0, await _queueRepo.GetCountAsync());
        Assert.Equal(0, await _detailsRepo.GetTotalChangesCountAsync());
    }

    [Fact]
    public async Task Worker_Startup_RecoversOrphanedProcessingRecordsAcrossBothQueues()
    {
        await _queueRepo.InitializeDatabaseAsync();
        await _authorityQueueRepo.InitializeDatabaseAsync();

        // Seed both queues with records simulating an ungraceful crash midway during processing
        await _queueRepo.UpsertDiscoveredSettlementsAsync(new[]
        {
            new DiscoveredSettlement(1001, "Crashed Settlement 1"),
            new DiscoveredSettlement(1002, "Normal Settlement 2")
        });
        await _authorityQueueRepo.UpsertDiscoveredAuthoritiesAsync(new[]
        {
            new DiscoveredLocalAuthority(2001, "Crashed Authority 1"),
            new DiscoveredLocalAuthority(2002, "Normal Authority 2")
        });

        // Set one in each queue directly to 'processing'
        await _queueRepo.MarkProcessingAsync(1001); // 1001 becomes 'processing'
        await _authorityQueueRepo.MarkProcessingAsync(2001); // 2001 becomes 'processing'

        Assert.Equal(1, await _queueRepo.GetCountByStatusAsync("processing"));
        Assert.Equal(1, await _queueRepo.GetCountByStatusAsync("pending"));
        Assert.Equal(1, await _authorityQueueRepo.GetCountByStatusAsync("processing"));
        Assert.Equal(1, await _authorityQueueRepo.GetCountByStatusAsync("pending"));

        var mockHandler = new MockHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.NotFound));
        var httpClient = new HttpClient(mockHandler);
        var crawler = new IndexDiscoveryCrawler(
            httpClient,
            new IndexParser(),
            _queueRepo,
            new CrawlerOptions { MaxPages = 0 },
            NullLogger<IndexDiscoveryCrawler>.Instance
        );
        var authorityCrawler = new LocalAuthorityDiscoveryCrawler(
            httpClient,
            new IndexParser(),
            _authorityQueueRepo,
            new LocalAuthorityCrawlerOptions { MaxPages = 0 },
            NullLogger<LocalAuthorityDiscoveryCrawler>.Instance
        );

        var cliOptions = new WorkerCliOptions
        {
            Entity = "all",
            Mode = "discover", // Discover mode initializes db and triggers crash recovery
            DatabasePath = _dbPath
        };

        var worker = new Worker(
            _queueRepo,
            crawler,
            null,
            null,
            null,
            cliOptions,
            new TestHostApplicationLifetime(),
            NullLogger<Worker>.Instance,
            authorityRepository: _authorityQueueRepo,
            authorityCrawler: authorityCrawler
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StartAsync(cts.Token);
        await worker.ExecuteTask!;

        // Both orphaned 'processing' items should be recovered to 'pending'
        Assert.Equal(0, await _queueRepo.GetCountByStatusAsync("processing"));
        Assert.Equal(2, await _queueRepo.GetCountByStatusAsync("pending"));

        Assert.Equal(0, await _authorityQueueRepo.GetCountByStatusAsync("processing"));
        Assert.Equal(2, await _authorityQueueRepo.GetCountByStatusAsync("pending"));
    }

    [Fact]
    public async Task Worker_GracefulCancellation_DrainsInFlightQueueWritesWithoutDataLoss()
    {
        await _queueRepo.InitializeDatabaseAsync();
        await _queueRepo.UpsertDiscoveredSettlementsAsync(new[]
        {
            new DiscoveredSettlement(3001, "Drain Settlement 1"),
            new DiscoveredSettlement(3002, "Drain Settlement 2")
        });

        var settlementCsv1 = "\"ΟΝΟΜΑΣΙΑ ΟΙΚΙΣΜΟΥ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\n\"Drain Settlement 1\";Αττικής;1930;;;01/01/1930;\"Προσάρτηση\"\r\n";
        var settlementCsv2 = "\"ΟΝΟΜΑΣΙΑ ΟΙΚΙΣΜΟΥ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\r\n\"Drain Settlement 2\";Αττικής;1935;;;01/01/1935;\"Προσάρτηση\"\r\n";

        var slowMockHandler = new MockHttpMessageHandler(req =>
        {
            var uri = req.RequestUri?.ToString() ?? string.Empty;
            if (uri.Contains("sid=3001"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(settlementCsv1, Encoding.UTF8, "text/csv")
                };
            }
            if (uri.Contains("sid=3002"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(settlementCsv2, Encoding.UTF8, "text/csv")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(slowMockHandler);
        var crawler = new IndexDiscoveryCrawler(
            httpClient,
            new IndexParser(),
            _queueRepo,
            new CrawlerOptions(),
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
            new IngestionOptions { Concurrency = 1, DelayMs = 100 },
            NullLogger<SettlementIngestionService>.Instance
        );

        var cliOptions = new WorkerCliOptions
        {
            Entity = "settlements",
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
            drainTimeout: TimeSpan.FromSeconds(5)
        );

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        // Cancel while ingestion is in-flight
        await Task.Delay(50);
        cts.Cancel();

        await worker.ExecuteTask!;

        // The worker should exit gracefully without throwing OperationCanceledException out of ExecuteAsync
        Assert.True(worker.ExecuteTask.IsCompletedSuccessfully);

        // Any items enqueued to the channel prior to cancellation must have been drained cleanly to SQLite
        var changesCount = await _detailsRepo.GetTotalChangesCountAsync();
        var completedCount = await _queueRepo.GetCountByStatusAsync("completed");
        Assert.Equal(changesCount, completedCount);
        Assert.Equal(0, channel.Count);
    }
}