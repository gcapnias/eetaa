namespace Eetaa.Worker.Discovery;

public sealed class LocalAuthorityCrawlerOptions
{
    public string BaseUrl { get; set; } = "https://eetaa.gr/eetaa/metaboles/dk_metaboles.php";
    public int Concurrency { get; set; } = 3;
    public int DelayMs { get; set; } = 150;
    public int StartPage { get; set; } = 1;
    public int? MaxPages { get; set; }
}
