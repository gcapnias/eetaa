using System.Net;

using Eetaa.Worker.Data;
using Eetaa.Worker.Discovery;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eetaa.Worker.Tests.Discovery;

public class LocalAuthorityDiscoveryCrawlerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteLocalAuthorityQueueRepository _repository;
    private readonly IndexParser _parser;

    public LocalAuthorityDiscoveryCrawlerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"eetaa_la_crawler_test_{Guid.NewGuid():N}.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);
        _repository = new SqliteLocalAuthorityQueueRepository(_connectionFactory);
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
    public async Task CrawlAsync_FetchesPages_ExtractsAuthorities_AndUpsertsIntoDatabase()
    {
        await _repository.InitializeDatabaseAsync();

        var page1Html = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "dk_index_page_1.html"));

        var page2Html = @"
            <html><body>
            <table id='results'>
                <tr><td><a href='dkmet_details.php?id=1050'>Δ. Πατρέων (Αχαΐας)</a></td></tr>
                <tr><td><a href='dkmet_details.php?id=1010'>Δ. Αθηναίων (Duplicate across pages)</a></td></tr>
                <tr><td><a href='dkmet_details.php?id=1060'>Κ. Κρυονερίου (Αττικής)</a></td></tr>
            </table>
            <div class='paginate'>
                <a href='dk_metaboles.php?ltr=ALL&pg=1'>1</a>
                <span class='current'>2</span>
                <a href='dk_metaboles.php?ltr=ALL&pg=937'>937</a>
            </div>
            </body></html>
        ";

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
                    Content = new StringContent(page2Html, System.Text.Encoding.UTF8, "text/html")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(handler);
        var options = new LocalAuthorityCrawlerOptions
        {
            BaseUrl = "https://eetaa.gr/eetaa/metaboles/dk_metaboles.php",
            StartPage = 1,
            MaxPages = 2,
            DelayMs = 0,
            Concurrency = 1
        };

        var crawler = new LocalAuthorityDiscoveryCrawler(
            httpClient,
            _parser,
            _repository,
            options,
            NullLogger<LocalAuthorityDiscoveryCrawler>.Instance);

        var result = await crawler.CrawlAsync();

        // Page 1 has 5 entries: 1010, 1020, 1030, 1010 (duplicate), 1040 -> 4 unique
        // Page 2 has 3 entries: 1050, 1010 (duplicate from page 1), 1060 -> 2 new unique (1050, 1060)
        // Total discovered unique in queue = 4 + 2 = 6
        Assert.Equal(2, result.PagesProcessed);
        Assert.Equal(6, result.TotalSettlementsDiscovered);

        var countInDb = await _repository.GetCountAsync();
        Assert.Equal(6, countInDb);
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handlerFunc) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handlerFunc(request));
        }
    }
}
