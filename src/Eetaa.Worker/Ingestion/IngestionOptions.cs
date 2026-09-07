namespace Eetaa.Worker.Ingestion;

public sealed class IngestionOptions
{
    public string CsvBaseUrl { get; set; } = "https://eetaa.gr/eetaa/metaboles/oikmet2csv.php";
    public int Concurrency { get; set; } = 3;
    public int DelayMs { get; set; } = 150;
    public int MaxRetries { get; set; } = 3;
    public int BaseRetryDelayMs { get; set; } = 200;
    public bool RetryFailed { get; set; }
    public bool Force { get; set; }
}
