namespace Eetaa.Worker.Ingestion;

public sealed record QueueSettlementItem(
    long SettlementId,
    string? NameHint,
    string Status,
    int AttemptCount
);