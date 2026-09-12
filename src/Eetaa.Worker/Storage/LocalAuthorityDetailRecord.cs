namespace Eetaa.Worker.Storage;

public sealed record LocalAuthorityDetailRecord(
    long AuthorityId,
    int ChangeOrder,
    AuthorityType? AuthorityType,
    string AuthorityName,
    string? Nomos,
    int? ChangeYear,
    string? FekNumber,
    string? FekIssue,
    string? FekDate,
    string? FekDateRaw,
    string ChangeDescription,
    string? RawRecord = null
);
