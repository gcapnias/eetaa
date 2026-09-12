using System.Net;

using Microsoft.Extensions.Logging;

namespace Eetaa.Worker.Ingestion;

public interface IRetryPolicy
{
    Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        long entityId = 0,
        CancellationToken cancellationToken = default);
}

public sealed class ExponentialBackoffRetryPolicy : IRetryPolicy
{
    private readonly int _maxRetries;
    private readonly int _baseDelayMs;
    private readonly ILogger<ExponentialBackoffRetryPolicy>? _logger;
    private readonly Random _jitter = new();

    public ExponentialBackoffRetryPolicy(
        int maxRetries = 3,
        int baseDelayMs = 200,
        ILogger<ExponentialBackoffRetryPolicy>? logger = null)
    {
        _maxRetries = maxRetries;
        _baseDelayMs = baseDelayMs;
        _logger = logger;
    }

    public async Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        long entityId = 0,
        CancellationToken cancellationToken = default)
    {
        var attempt = 0;

        while (true)
        {
            attempt++;
            try
            {
                return await operation(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt <= _maxRetries)
            {
                // Exponential backoff: baseDelay * 2^(attempt - 1) + jitter
                var backoffFactor = Math.Pow(2, attempt - 1);
                var baseDelay = (int)(_baseDelayMs * backoffFactor);
                int jitterMs;
                lock (_jitter)
                {
                    jitterMs = _jitter.Next(0, Math.Max(1, _baseDelayMs / 2));
                }
                var totalDelay = TimeSpan.FromMilliseconds(baseDelay + jitterMs);

                _logger?.LogWarning(
                    ex,
                    "Transient error downloading entity {EntityId} on attempt {Attempt}/{MaxRetries}. Retrying in {DelayMs}ms.",
                    entityId, attempt, _maxRetries, (int)totalDelay.TotalMilliseconds);

                await Task.Delay(totalDelay, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogError(
                    ex,
                    "Permanent or exhausted failure for entity {EntityId} on attempt {Attempt}/{MaxRetries}.",
                    entityId, attempt, _maxRetries);
                throw;
            }
        }
    }

    public static bool IsTransient(Exception ex)
    {
        if (ex is HttpRequestException httpEx)
        {
            if (httpEx.StatusCode.HasValue)
            {
                var code = (int)httpEx.StatusCode.Value;
                // 429 Too Many Requests, 500, 502 Bad Gateway, 503 Service Unavailable, 504 Gateway Timeout
                return code == 429 || code == 500 || code == 502 || code == 503 || code == 504;
            }

            // Connection reset, DNS failure, etc.
            return true;
        }

        if (ex is TimeoutException || ex is TaskCanceledException)
        {
            return true;
        }

        return false;
    }
}