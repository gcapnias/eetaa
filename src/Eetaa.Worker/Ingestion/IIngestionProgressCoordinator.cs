namespace Eetaa.Worker.Ingestion;

public interface IIngestionProgressCoordinator : IDisposable
{
    int Total { get; }
    int Downloaded { get; }
    int Persisted { get; }
    int Failed { get; }
    string EntityLabel { get; }

    void Start(int totalCount, Func<int> getBufferCount, string? entityLabel = null);
    void RecordDownloaded();
    void RecordPersisted();
    void RecordFailed();
    void Complete();
}