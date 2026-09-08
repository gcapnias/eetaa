using System.Net;

using Eetaa.Worker.Data;
using Eetaa.Worker.Discovery;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eetaa.Worker.Tests.Discovery;

public class IndexDiscoveryCrawlerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteQueueRepository _repository;
    private readonly IndexParser _parser;

    public IndexDiscoveryCrawlerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"eetaa_crawler_test_{Guid.NewGuid():N}.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);
        _repository = new SqliteQueueRepository(_connectionFactory);
        _parser = new IndexParser();
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
        }
    }

    [Fact]
    public async Task CrawlAsync_FetchesPages_ExtractsSettlements_AndUpsertsIntoDatabase()
    {
        await _repository.InitializeDatabaseAsync();

        var page1Html = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "index_page_1.html"));
        var lastPageHtml = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "index_page_last.html"));

        // Setup mock handler
        var handler = new MockHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!.ToString();
            if (uri.Contains("pg=1"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(page1Html, System.Text.Encoding.UTF8, "text/html")
                };
            }
            if (uri.Contains("pg=2"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(lastPageHtml, System.Text.Encoding.UTF8, "text/html")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(handler);
        var options = new CrawlerOptions
        {
            BaseUrl = "https://eetaa.gr/eetaa/metaboles/oik_metaboles.php",
            StartPage = 1,
            MaxPages = 2,
            DelayMs = 0,
            Concurrency = 1
        };

        var crawler = new IndexDiscoveryCrawler(
            httpClient,
            _parser,
            _repository,
            options,
            NullLogger<IndexDiscoveryCrawler>.Instance);

        var result = await crawler.CrawlAsync();

        // Page 1 has 15 items with duplicates:
        // Unique IDs on page 1: 25734, 3402, 3377, 17001, 15985, 27357, 28333, 10189, 14393, 25298, 13941, 23023 = 12 unique
        // Page 2 (lastPageHtml) has 6 items with duplicates:
        // Unique IDs on page 2: 11307, 10502, 11430, 10766, 17321 = 5 unique
        // Total discovered in queue = 12 + 5 = 17 unique settlements
        Assert.Equal(2, result.PagesProcessed);
        Assert.Equal(17, result.TotalSettlementsDiscovered);

        var countInDb = await _repository.GetCountAsync();
        Assert.Equal(17, countInDb);
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handlerFunc) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handlerFunc(request));
        }
    }
}