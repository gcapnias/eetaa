namespace Eetaa.Worker.Storage;

public sealed record LocalAuthorityPayload(
    long AuthorityId,
    IReadOnlyList<LocalAuthorityDetailRecord> Changes,
    string? ErrorMessage = null
);
