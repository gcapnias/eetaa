using Eetaa.Worker.Ingestion;

using Microsoft.Extensions.Logging;

namespace Eetaa.Worker.Storage;

public interface ISingleWriterStorageEngine
{
    Task ProcessChannelAsync(CancellationToken cancellationToken = default);
}

public sealed class SingleWriterStorageEngine : ISingleWriterStorageEngine
{
    private readonly ISingleWriterChannel _channel;
    private readonly ISettlementDetailsRepository _repository;
    private readonly ILogger<SingleWriterStorageEngine>? _logger;
    private readonly IIngestionProgressCoordinator? _progressCoordinator;

    public SingleWriterStorageEngine(
        ISingleWriterChannel channel,
        ISettlementDetailsRepository repository,
        ILogger<SingleWriterStorageEngine>? logger = null,
        IIngestionProgressCoordinator? progressCoordinator = null)
    {
        _channel = channel;
        _repository = repository;
        _logger = logger;
        _progressCoordinator = progressCoordinator;
    }

    public async Task ProcessChannelAsync(CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("SingleWriterStorageEngine consumer started.");

        var processedCount = 0L;
        var errorCount = 0L;

        try
        {
            await foreach (var payload in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await _repository.SaveSettlementChangesAsync(payload, cancellationToken);
                    if (!string.IsNullOrEmpty(payload.ErrorMessage))
                    {
                        RecordFailure(ref errorCount, payload.SettlementId, payload.ErrorMessage);
                    }
                    else
                    {
                        processedCount++;
                        _progressCoordinator?.RecordPersisted();
                        _logger?.LogDebug("Saved {Count} changes for settlement {SettlementId}", payload.Changes.Count, payload.SettlementId);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger?.LogInformation("Cancellation requested during settlement save.");
                    throw;
                }
                catch (Exception ex)
                {
                    RecordFailure(ref errorCount, payload.SettlementId, ex.Message, ex);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger?.LogInformation("SingleWriterStorageEngine processing canceled.");
        }
        finally
        {
            _logger?.LogInformation(
                "SingleWriterStorageEngine consumer stopped. Total processed: {Processed}, errors: {Errors}",
                processedCount, errorCount);
        }
    }

    private void RecordFailure(ref long errorCount, long settlementId, string errorMessage, Exception? ex = null)
    {
        errorCount++;
        _progressCoordinator?.RecordFailed();
        if (ex != null)
        {
            _logger?.LogError(ex, "Unexpected error saving settlement {SettlementId}", settlementId);
        }
        else
        {
            _logger?.LogWarning("Recorded failure for settlement {SettlementId}: {Error}", settlementId, errorMessage);
        }
    }
}