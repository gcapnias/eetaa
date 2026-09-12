using System.Collections.Concurrent;

using Eetaa.Worker.Data;

using Microsoft.Extensions.Logging;

namespace Eetaa.Worker.Discovery;

public sealed class LocalAuthorityDiscoveryCrawler(
    HttpClient httpClient,
    IndexParser parser,
    ISqliteLocalAuthorityQueueRepository repository,
    LocalAuthorityCrawlerOptions options,
    ILogger<LocalAuthorityDiscoveryCrawler> logger) : ILocalAuthorityDiscoveryCrawler
{
    public async Task<CrawlResult> CrawlAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting local authority index discovery crawl from base URL: {BaseUrl}", options.BaseUrl);

        // Fetch page 1 (or start page) to determine total pages if not explicitly bounded
        var startPage = Math.Max(1, options.StartPage);
        var initialUrl = BuildPageUrl(startPage);

        logger.LogInformation("Fetching initial page {Page} to assess pagination: {Url}", startPage, initialUrl);
        var initialHtml = await FetchPageHtmlAsync(initialUrl, cancellationToken);
        var detectedTotalPages = parser.ParseTotalPages(initialHtml);

        var endPage = options.MaxPages.HasValue
            ? Math.Min(startPage + options.MaxPages.Value - 1, detectedTotalPages)
            : detectedTotalPages;

        logger.LogInformation(
            "Local authority discovery range: pages {StartPage} to {EndPage} (detected total: {DetectedTotal})",
            startPage, endPage, detectedTotalPages);

        var pagesProcessed = 0;
        var totalDiscovered = 0;

        // Process first page authorities directly
        var firstPageAuthorities = parser.ParseLocalAuthorities(initialHtml);
        var deduplicatedFirst = parser.Deduplicate(firstPageAuthorities).ToList();
        var insertedFirst = await repository.UpsertDiscoveredAuthoritiesAsync(deduplicatedFirst, cancellationToken);

        var seenAuthorityIds = new HashSet<int>(deduplicatedFirst.Select(a => a.AuthorityId));

        pagesProcessed++;
        totalDiscovered += insertedFirst;

        logger.LogInformation(
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
            MaxDegreeOfParallelism = Math.Max(1, options.Concurrency),
            CancellationToken = cancellationToken
        };

        var pageResults = new ConcurrentDictionary<int, IReadOnlyList<DiscoveredLocalAuthority>>();
        var nextExpectedPage = startPage + 1;
        using var drainLock = new SemaphoreSlim(1, 1);

        await Parallel.ForEachAsync(remainingPages, parallelOptions, async (page, ct) =>
        {
            if (options.DelayMs > 0)
            {
                await Task.Delay(options.DelayMs, ct);
            }

            var url = BuildPageUrl(page);
            string? html = null;
            try
            {
                html = await FetchPageHtmlAsync(url, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to fetch index page {Page} at {Url}", page, url);
            }

            var authorities = html is not null
                ? parser.ParseLocalAuthorities(html)
                : (IReadOnlyList<DiscoveredLocalAuthority>)Array.Empty<DiscoveredLocalAuthority>();

            if (html is not null && authorities.Count == 0)
            {
                logger.LogWarning("Page {Page} yielded 0 local authorities. Results table might be empty or ended.", page);
            }

            pageResults[page] = authorities;

            // Drain consecutive pages in strict ascending page order to preserve first-seen name hints
            await drainLock.WaitAsync(ct);
            try
            {
                while (pageResults.TryRemove(nextExpectedPage, out var pendingAuthorities))
                {
                    if (pendingAuthorities.Count > 0)
                    {
                        var uniqueAuthorities = new List<DiscoveredLocalAuthority>();
                        foreach (var auth in pendingAuthorities)
                        {
                            if (seenAuthorityIds.Add(auth.AuthorityId))
                            {
                                uniqueAuthorities.Add(auth);
                            }
                        }

                        if (uniqueAuthorities.Count > 0)
                        {
                            var inserted = await repository.UpsertDiscoveredAuthoritiesAsync(uniqueAuthorities, ct);
                            totalDiscovered += inserted;
                        }
                    }

                    pagesProcessed++;
                    nextExpectedPage++;

                    if (pagesProcessed % 25 == 0 || pagesProcessed == (endPage - startPage + 1))
                    {
                        logger.LogInformation(
                            "Progress: {Pages}/{TotalPages} pages processed. Discovered {Discovered} local authorities so far.",
                            pagesProcessed, endPage - startPage + 1, totalDiscovered);
                    }
                }
            }
            finally
            {
                drainLock.Release();
            }
        });

        logger.LogInformation(
            "Local authority discovery completed. Total pages processed: {Pages}, total local authorities upserted: {Discovered}",
            pagesProcessed, totalDiscovered);

        return new CrawlResult(pagesProcessed, totalDiscovered);
    }

    private string BuildPageUrl(int page)
    {
        var separator = options.BaseUrl.Contains('?') ? "&" : "?";
        var query = options.BaseUrl.Contains("ltr=", StringComparison.OrdinalIgnoreCase)
            ? $"pg={page}"
            : $"ltr=ALL&pg={page}";

        return $"{options.BaseUrl}{separator}{query}";
    }

    private async Task<string> FetchPageHtmlAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; EetaaWorker/1.0; +https://github.com/eetaa)");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
