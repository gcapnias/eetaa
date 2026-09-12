namespace Eetaa.Worker.Discovery;

public sealed record CrawlResult(int PagesProcessed, int TotalSettlementsDiscovered)
{
    public int TotalDiscovered => TotalSettlementsDiscovered;
}