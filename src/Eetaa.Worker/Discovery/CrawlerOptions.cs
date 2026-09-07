namespace Eetaa.Worker.Discovery;

public sealed class CrawlerOptions
{
    public string BaseUrl { get; set; } = "https://eetaa.gr/eetaa/metaboles/oik_metaboles.php";
    public int Concurrency { get; set; } = 3;
    public int DelayMs { get; set; } = 150;
    public int StartPage { get; set; } = 1;
    public int? MaxPages { get; set; }
}
