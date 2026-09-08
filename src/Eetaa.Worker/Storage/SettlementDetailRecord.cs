namespace Eetaa.Worker.Storage;

public sealed record SettlementDetailRecord(
    long SettlementId,
    int ChangeOrder,
    string SettlementName,
    string? Nomos,
    int? ChangeYear,
    string? FekNumber,
    string? FekIssue,
    string? FekDate,
    string? FekDateRaw,
    string ChangeDescription,
    string? RawRecord = null
);