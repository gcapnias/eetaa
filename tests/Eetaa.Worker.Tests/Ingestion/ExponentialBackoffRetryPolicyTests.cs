using System.Net;

using Eetaa.Worker.Ingestion;

using Xunit;

namespace Eetaa.Worker.Tests.Ingestion;

public class ExponentialBackoffRetryPolicyTests
{
    [Fact]
    public async Task ExecuteWithRetryAsync_SucceedsOnFirstAttempt()
    {
        var policy = new ExponentialBackoffRetryPolicy(maxRetries: 3, baseDelayMs: 10);
        var attempts = 0;

        var result = await policy.ExecuteWithRetryAsync(ct =>
        {
            attempts++;
            return Task.FromResult("OK");
        }, settlementId: 100);

        Assert.Equal("OK", result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_RetriesTransient502AndSucceeds()
    {
        var policy = new ExponentialBackoffRetryPolicy(maxRetries: 3, baseDelayMs: 10);
        var attempts = 0;

        var result = await policy.ExecuteWithRetryAsync(ct =>
        {
            attempts++;
            if (attempts < 3)
            {
                throw new HttpRequestException("Bad Gateway", null, HttpStatusCode.BadGateway);
            }
            return Task.FromResult("Recovered");
        }, settlementId: 200);

        Assert.Equal("Recovered", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_RetriesTransient429AndSucceeds()
    {
        var policy = new ExponentialBackoffRetryPolicy(maxRetries: 3, baseDelayMs: 10);
        var attempts = 0;

        var result = await policy.ExecuteWithRetryAsync(ct =>
        {
            attempts++;
            if (attempts < 2)
            {
                throw new HttpRequestException("Rate limited", null, HttpStatusCode.TooManyRequests);
            }
            return Task.FromResult("SuccessAfter429");
        }, settlementId: 300);

        Assert.Equal("SuccessAfter429", result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_ThrowsWhenRetriesExhausted()
    {
        var policy = new ExponentialBackoffRetryPolicy(maxRetries: 2, baseDelayMs: 10);
        var attempts = 0;

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            policy.ExecuteWithRetryAsync<string>(ct =>
            {
                attempts++;
                throw new HttpRequestException("Gateway Timeout", null, HttpStatusCode.GatewayTimeout);
            }, settlementId: 400));

        Assert.Equal(3, attempts); // 1 initial + 2 retries
        Assert.Equal(HttpStatusCode.GatewayTimeout, ex.StatusCode);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_DoesNotRetryNonTransientErrors()
    {
        var policy = new ExponentialBackoffRetryPolicy(maxRetries: 3, baseDelayMs: 10);
        var attempts = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            policy.ExecuteWithRetryAsync<string>(ct =>
            {
                attempts++;
                throw new HttpRequestException("Not Found", null, HttpStatusCode.NotFound);
            }, settlementId: 500));

        Assert.Equal(1, attempts);
    }
}