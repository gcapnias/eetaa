namespace Eetaa.Worker.Ingestion;

public interface IIngestionProgressCoordinator : IDisposable
{
    int Total { get; }
    int Downloaded { get; }
    int Persisted { get; }
    int Failed { get; }

    void Start(int totalCount, Func<int> getBufferCount);
    void RecordDownloaded();
    void RecordPersisted();
    void RecordFailed();
    void Complete();
}