namespace Eetaa.Worker.Ingestion;

public sealed record QueueLocalAuthorityItem(long AuthorityId, string? NameHint, string Status, int AttemptCount);
