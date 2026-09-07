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

    public SingleWriterStorageEngine(
        ISingleWriterChannel channel,
        ISettlementDetailsRepository repository,
        ILogger<SingleWriterStorageEngine>? logger = null)
    {
        _channel = channel;
        _repository = repository;
        _logger = logger;
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
                        errorCount++;
                        _logger?.LogWarning("Recorded failure for settlement {SettlementId}: {Error}", payload.SettlementId, payload.ErrorMessage);
                    }
                    else
                    {
                        processedCount++;
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
                    errorCount++;
                    _logger?.LogError(ex, "Unexpected error saving settlement {SettlementId}", payload.SettlementId);
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
}
