using Eetaa.Worker.Ingestion;

using Microsoft.Extensions.Logging;

namespace Eetaa.Worker.Storage;

public interface ILocalAuthorityStorageEngine : ISingleWriterStorageEngine
{
}

public sealed class LocalAuthorityStorageEngine : ILocalAuthorityStorageEngine
{
    private readonly ISingleWriterChannel<LocalAuthorityPayload> _channel;
    private readonly ILocalAuthorityDetailsRepository _repository;
    private readonly ILogger<LocalAuthorityStorageEngine>? _logger;
    private readonly IIngestionProgressCoordinator? _progressCoordinator;

    public LocalAuthorityStorageEngine(
        ISingleWriterChannel<LocalAuthorityPayload> channel,
        ILocalAuthorityDetailsRepository repository,
        ILogger<LocalAuthorityStorageEngine>? logger = null,
        IIngestionProgressCoordinator? progressCoordinator = null)
    {
        _channel = channel;
        _repository = repository;
        _logger = logger;
        _progressCoordinator = progressCoordinator;
    }

    public async Task ProcessChannelAsync(CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("LocalAuthorityStorageEngine consumer started.");

        var processedCount = 0L;
        var errorCount = 0L;

        try
        {
            await foreach (var payload in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await _repository.SaveLocalAuthorityChangesAsync(payload, cancellationToken);
                    if (!string.IsNullOrEmpty(payload.ErrorMessage))
                    {
                        RecordFailure(ref errorCount, payload.AuthorityId, payload.ErrorMessage);
                    }
                    else
                    {
                        processedCount++;
                        _progressCoordinator?.RecordPersisted();
                        _logger?.LogDebug("Saved {Count} changes for local authority {AuthorityId}", payload.Changes.Count, payload.AuthorityId);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger?.LogInformation("Cancellation requested during local authority save.");
                    throw;
                }
                catch (Exception ex)
                {
                    RecordFailure(ref errorCount, payload.AuthorityId, ex.Message, ex);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger?.LogInformation("LocalAuthorityStorageEngine processing canceled.");
        }
        finally
        {
            _logger?.LogInformation(
                "LocalAuthorityStorageEngine consumer stopped. Total processed: {Processed}, errors: {Errors}",
                processedCount, errorCount);
        }
    }

    private void RecordFailure(ref long errorCount, long authorityId, string errorMessage, Exception? ex = null)
    {
        errorCount++;
        _progressCoordinator?.RecordFailed();
        if (ex != null)
        {
            _logger?.LogError(ex, "Unexpected error saving local authority {AuthorityId}", authorityId);
        }
        else
        {
            _logger?.LogWarning("Recorded failure for local authority {AuthorityId}: {Error}", authorityId, errorMessage);
        }
    }
}
