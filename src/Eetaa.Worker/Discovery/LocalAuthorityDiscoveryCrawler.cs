using Eetaa.Worker.Data;

using Microsoft.Extensions.Logging;

namespace Eetaa.Worker.Discovery;

public sealed class LocalAuthorityDiscoveryCrawler : ILocalAuthorityDiscoveryCrawler
{
    private readonly HttpClient _httpClient;
    private readonly IndexParser _parser;
    private readonly ISqliteLocalAuthorityQueueRepository _repository;
    private readonly LocalAuthorityCrawlerOptions _options;
    private readonly ILogger<LocalAuthorityDiscoveryCrawler> _logger;

    public LocalAuthorityDiscoveryCrawler(
        HttpClient httpClient,
        IndexParser parser,
        ISqliteLocalAuthorityQueueRepository repository,
        LocalAuthorityCrawlerOptions options,
        ILogger<LocalAuthorityDiscoveryCrawler> logger)
    {
        _httpClient = httpClient;
        _parser = parser;
        _repository = repository;
        _options = options;
        _logger = logger;
    }

    public async Task<CrawlResult> CrawlAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting local authority index discovery crawl from base URL: {BaseUrl}", _options.BaseUrl);

        // Fetch page 1 (or start page) to determine total pages if not explicitly bounded
        var startPage = Math.Max(1, _options.StartPage);
        var initialUrl = BuildPageUrl(startPage);

        _logger.LogInformation("Fetching initial page {Page} to assess pagination: {Url}", startPage, initialUrl);
        var initialHtml = await FetchPageHtmlAsync(initialUrl, cancellationToken);
        var detectedTotalPages = _parser.ParseTotalPages(initialHtml);

        var endPage = _options.MaxPages.HasValue
            ? Math.Min(startPage + _options.MaxPages.Value - 1, detectedTotalPages)
            : detectedTotalPages;

        _logger.LogInformation(
            "Local authority discovery range: pages {StartPage} to {EndPage} (detected total: {DetectedTotal})",
            startPage, endPage, detectedTotalPages);

        var pagesProcessed = 0;
        var totalDiscovered = 0;

        // Process first page authorities directly
        var firstPageAuthorities = _parser.ParseLocalAuthorities(initialHtml);
        var deduplicatedFirst = _parser.Deduplicate(firstPageAuthorities).ToList();
        var insertedFirst = await _repository.UpsertDiscoveredAuthoritiesAsync(deduplicatedFirst, cancellationToken);

        pagesProcessed++;
        totalDiscovered += insertedFirst;

        _logger.LogInformation(
            "Page {Page}/{EndPage}: parsed {Found} local authorities ({Unique} unique), upserted {Inserted} new. Cumulative: {Total}",
            startPage, endPage, firstPageAuthorities.Count, deduplicatedFirst.Count, insertedFirst, totalDiscovered);

        if (startPage >= endPage)
        {
            return new CrawlResult(pagesProcessed, totalDiscovered);
        }

        // Prepare remaining pages
        var remainingPages = Enumerable.Range(startPage + 1, endPage - startPage).ToList();
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _options.Concurrency),
            CancellationToken = cancellationToken
        };

        using var writeLock = new SemaphoreSlim(1, 1);

        await Parallel.ForEachAsync(remainingPages, parallelOptions, async (page, ct) =>
        {
            if (_options.DelayMs > 0)
            {
                await Task.Delay(_options.DelayMs, ct);
            }

            var url = BuildPageUrl(page);
            string html;
            try
            {
                html = await FetchPageHtmlAsync(url, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to fetch index page {Page} at {Url}", page, url);
                return;
            }

            var authorities = _parser.ParseLocalAuthorities(html);
            if (authorities.Count == 0)
            {
                _logger.LogWarning("Page {Page} yielded 0 local authorities. Results table might be empty or ended.", page);
                await writeLock.WaitAsync(ct);
                try
                {
                    pagesProcessed++;
                }
                finally
                {
                    writeLock.Release();
                }
                return;
            }

            var uniqueAuthorities = _parser.Deduplicate(authorities).ToList();

            // Synchronize write transactions to ensure single-writer discipline for SQLite
            await writeLock.WaitAsync(ct);
            try
            {
                var inserted = await _repository.UpsertDiscoveredAuthoritiesAsync(uniqueAuthorities, ct);
                pagesProcessed++;
                totalDiscovered += inserted;

                if (pagesProcessed % 25 == 0 || pagesProcessed == (endPage - startPage + 1))
                {
                    _logger.LogInformation(
                        "Progress: {Pages}/{TotalPages} pages processed. Discovered {Discovered} local authorities so far.",
                        pagesProcessed, endPage - startPage + 1, totalDiscovered);
                }
            }
            finally
            {
                writeLock.Release();
            }
        });

        _logger.LogInformation(
            "Local authority discovery completed. Total pages processed: {Pages}, total local authorities upserted: {Discovered}",
            pagesProcessed, totalDiscovered);

        return new CrawlResult(pagesProcessed, totalDiscovered);
    }

    private string BuildPageUrl(int page)
    {
        var separator = _options.BaseUrl.Contains('?') ? "&" : "?";
        var query = _options.BaseUrl.Contains("ltr=", StringComparison.OrdinalIgnoreCase)
            ? $"pg={page}"
            : $"ltr=ALL&pg={page}";

        return $"{_options.BaseUrl}{separator}{query}";
    }

    private async Task<string> FetchPageHtmlAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; EetaaWorker/1.0; +https://github.com/eetaa)");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
