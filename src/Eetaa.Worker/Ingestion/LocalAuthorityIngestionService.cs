using Eetaa.Worker.Data;
using Eetaa.Worker.Storage;

using Microsoft.Extensions.Logging;

namespace Eetaa.Worker.Ingestion;

public interface ILocalAuthorityIngestionService
{
    Task<IngestionResult> IngestAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalAuthorityIngestionService : ILocalAuthorityIngestionService
{
    private readonly HttpClient _httpClient;
    private readonly ISqliteLocalAuthorityQueueRepository _queueRepository;
    private readonly ILocalAuthorityCsvParser _csvParser;
    private readonly ISingleWriterChannel<LocalAuthorityPayload> _channel;
    private readonly IRetryPolicy _retryPolicy;
    private readonly LocalAuthorityIngestionOptions _options;
    private readonly ILogger<LocalAuthorityIngestionService> _logger;
    private readonly IIngestionProgressCoordinator? _progressCoordinator;

    public LocalAuthorityIngestionService(
        HttpClient httpClient,
        ISqliteLocalAuthorityQueueRepository queueRepository,
        ILocalAuthorityCsvParser csvParser,
        ISingleWriterChannel<LocalAuthorityPayload> channel,
        IRetryPolicy retryPolicy,
        LocalAuthorityIngestionOptions options,
        ILogger<LocalAuthorityIngestionService> logger,
        IIngestionProgressCoordinator? progressCoordinator = null)
    {
        _httpClient = httpClient;
        _queueRepository = queueRepository;
        _csvParser = csvParser;
        _channel = channel;
        _retryPolicy = retryPolicy;
        _options = options;
        _logger = logger;
        _progressCoordinator = progressCoordinator;
    }

    public async Task<IngestionResult> IngestAsync(CancellationToken cancellationToken = default)
    {
        // 1. Startup crash recovery: reset any orphaned in-flight 'processing' records
        var resetCount = await _queueRepository.ResetInFlightProcessingAsync(cancellationToken);
        if (resetCount > 0)
        {
            _logger.LogInformation("Reset {ResetCount} orphaned in-flight 'processing' local authority record(s) to 'pending'", resetCount);
        }

        // 2. Fetch queue items to process
        var pendingItems = await _queueRepository.GetPendingAuthoritiesAsync(
            includeFailed: _options.RetryFailed,
            forceAll: _options.Force,
            limit: null,
            cancellationToken: cancellationToken);

        _logger.LogInformation("Found {Count} eligible local authority(ies) for CSV download.", pendingItems.Count);

        _progressCoordinator?.Start(pendingItems.Count, () => _channel.Count, "local authority");

        if (pendingItems.Count == 0)
        {
            return new IngestionResult(0, 0, 0);
        }

        var concurrency = Math.Max(1, _options.Concurrency);
        using var throttler = new SemaphoreSlim(concurrency, concurrency);

        var succeededCount = 0;
        var failedCount = 0;

        var downloadTasks = pendingItems.Select(async item =>
        {
            await throttler.WaitAsync(cancellationToken);
            try
            {
                var success = await ProcessSingleAuthorityAsync(item, cancellationToken);
                if (success)
                {
                    Interlocked.Increment(ref succeededCount);
                }
                else
                {
                    Interlocked.Increment(ref failedCount);
                }

                // Polite rate throttling between requests
                if (_options.DelayMs > 0)
                {
                    await Task.Delay(_options.DelayMs, cancellationToken);
                }
            }
            finally
            {
                throttler.Release();
            }
        });

        await Task.WhenAll(downloadTasks);

        _logger.LogInformation(
            "Local authority ingestion completed: {Total} processed, {Succeeded} succeeded, {Failed} failed.",
            pendingItems.Count, succeededCount, failedCount);

        return new IngestionResult(pendingItems.Count, succeededCount, failedCount);
    }

    private async Task<bool> ProcessSingleAuthorityAsync(QueueLocalAuthorityItem item, CancellationToken cancellationToken)
    {
        await _queueRepository.MarkProcessingAsync(item.AuthorityId, cancellationToken);

        var url = $"{_options.CsvBaseUrl.TrimEnd('/')}?sid={item.AuthorityId}";

        try
        {
            var payload = await _retryPolicy.ExecuteWithRetryAsync(async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"HTTP request failed with status code {(int)response.StatusCode} ({response.ReasonPhrase})",
                        inner: null,
                        statusCode: response.StatusCode);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var payload = _csvParser.Parse(item.AuthorityId, stream);
                return payload;
            }, item.AuthorityId, cancellationToken);

            await _channel.EnqueueAsync(payload, cancellationToken);
            _progressCoordinator?.RecordDownloaded();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download/parse CSV for local authority {AuthorityId}", item.AuthorityId);
            var failurePayload = new LocalAuthorityPayload(item.AuthorityId, Array.Empty<LocalAuthorityDetailRecord>(), ex.Message);
            await _channel.EnqueueAsync(failurePayload, cancellationToken);
            return false;
        }
    }
}
