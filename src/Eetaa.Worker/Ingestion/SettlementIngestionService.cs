using System.Threading.Channels;

using Eetaa.Worker.Data;
using Eetaa.Worker.Storage;

using Microsoft.Extensions.Logging;

namespace Eetaa.Worker.Ingestion;

public interface ISettlementIngestionService
{
    Task<IngestionResult> IngestAsync(CancellationToken cancellationToken = default);
}

public sealed class SettlementIngestionService : ISettlementIngestionService
{
    private readonly HttpClient _httpClient;
    private readonly ISqliteQueueRepository _queueRepository;
    private readonly ISettlementCsvParser _csvParser;
    private readonly ISingleWriterChannel _channel;
    private readonly IRetryPolicy _retryPolicy;
    private readonly IngestionOptions _options;
    private readonly ILogger<SettlementIngestionService> _logger;
    private readonly IIngestionProgressCoordinator? _progressCoordinator;

    public SettlementIngestionService(
        HttpClient httpClient,
        ISqliteQueueRepository queueRepository,
        ISettlementCsvParser csvParser,
        ISingleWriterChannel channel,
        IRetryPolicy retryPolicy,
        IngestionOptions options,
        ILogger<SettlementIngestionService> logger,
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
            _logger.LogInformation("Reset {ResetCount} orphaned in-flight 'processing' record(s) to 'pending'", resetCount);
        }

        // 2. Fetch queue items to process
        var pendingItems = await _queueRepository.GetPendingSettlementsAsync(
            includeFailed: _options.RetryFailed,
            forceAll: _options.Force,
            limit: null,
            cancellationToken: cancellationToken);

        _logger.LogInformation("Found {Count} eligible settlement(s) for CSV download.", pendingItems.Count);

        _progressCoordinator?.Start(pendingItems.Count, () => _channel.Count);

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
                var success = await ProcessSingleSettlementAsync(item, cancellationToken);
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
            "Ingestion completed: {Total} processed, {Succeeded} succeeded, {Failed} failed.",
            pendingItems.Count, succeededCount, failedCount);

        return new IngestionResult(pendingItems.Count, succeededCount, failedCount);
    }

    private async Task<bool> ProcessSingleSettlementAsync(QueueSettlementItem item, CancellationToken cancellationToken)
    {
        await _queueRepository.MarkProcessingAsync(item.SettlementId, cancellationToken);

        var url = $"{_options.CsvBaseUrl.TrimEnd('/')}?sid={item.SettlementId}";

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
                var payload = _csvParser.Parse(item.SettlementId, stream);
                return payload;
            }, item.SettlementId, cancellationToken);

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
            _logger.LogError(ex, "Failed to download/parse CSV for settlement {SettlementId}", item.SettlementId);
            var failurePayload = new SettlementPayload(item.SettlementId, Array.Empty<SettlementDetailRecord>(), ex.Message);
            await _channel.EnqueueAsync(failurePayload, cancellationToken);
            return false;
        }
    }
}