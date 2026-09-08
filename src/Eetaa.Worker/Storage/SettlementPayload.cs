namespace Eetaa.Worker.Storage;

public sealed record SettlementPayload(
    long SettlementId,
    IReadOnlyList<SettlementDetailRecord> Changes,
    string? ErrorMessage = null
);