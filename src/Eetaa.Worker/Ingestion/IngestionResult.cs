namespace Eetaa.Worker.Ingestion;

public sealed record IngestionResult(
    int TotalProcessed,
    int Succeeded,
    int Failed
);