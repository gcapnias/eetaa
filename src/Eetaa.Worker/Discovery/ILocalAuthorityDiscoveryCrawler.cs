namespace Eetaa.Worker.Discovery;

public interface ILocalAuthorityDiscoveryCrawler
{
    Task<CrawlResult> CrawlAsync(CancellationToken cancellationToken = default);
}
