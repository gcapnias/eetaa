using System.Threading.Channels;

using Eetaa.Worker.Configuration;
using Eetaa.Worker.Data;
using Eetaa.Worker.Discovery;
using Eetaa.Worker.Ingestion;
using Eetaa.Worker.Storage;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Eetaa.Worker;

public sealed class Worker : BackgroundService
{
    private readonly ISqliteQueueRepository _repository;
    private readonly IIndexDiscoveryCrawler _discoveryCrawler;
    private readonly ISettlementIngestionService _ingestionService;
    private readonly ISingleWriterChannel _channel;
    private readonly ISingleWriterStorageEngine _storageEngine;
    private readonly IIngestionProgressCoordinator? _progressCoordinator;
    private readonly WorkerCliOptions _cliOptions;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<Worker> _logger;
    private readonly TimeSpan _drainTimeout;

    public Worker(
        ISqliteQueueRepository repository,
        IIndexDiscoveryCrawler discoveryCrawler,
        ISettlementIngestionService ingestionService,
        ISingleWriterChannel channel,
        ISingleWriterStorageEngine storageEngine,
        WorkerCliOptions cliOptions,
        IHostApplicationLifetime lifetime,
        ILogger<Worker> logger,
        IIngestionProgressCoordinator? progressCoordinator = null,
        TimeSpan? drainTimeout = null)
    {
        _repository = repository;
        _discoveryCrawler = discoveryCrawler;
        _ingestionService = ingestionService;
        _channel = channel;
        _storageEngine = storageEngine;
        _cliOptions = cliOptions;
        _lifetime = lifetime;
        _logger = logger;
        _progressCoordinator = progressCoordinator;
        _drainTimeout = drainTimeout ?? TimeSpan.FromSeconds(30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Worker starting in mode: {Mode}, db: {Db}", _cliOptions.Mode, _cliOptions.DatabasePath);

            // 1. Initialize SQLite schema and PRAGMAs
            await _repository.InitializeDatabaseAsync(stoppingToken);
            _logger.LogInformation("SQLite database initialized at {DbPath}", _cliOptions.DatabasePath);

            // 2. In-flight recovery: reset any 'processing' items left behind by previous crashes
            var resetCount = await _repository.ResetInFlightProcessingAsync(stoppingToken);
            if (resetCount > 0)
            {
                _logger.LogInformation("Reset {ResetCount} orphaned in-flight 'processing' settlement(s) to 'pending'", resetCount);
            }

            var initialCount = await _repository.GetCountAsync(stoppingToken);
            _logger.LogInformation("Current settlement_queue record count: {Count}", initialCount);

            // 3. Execute according to mode
            if (_cliOptions.Mode is "all" or "discover")
            {
                _logger.LogInformation("Executing Index Discovery phase...");
                var result = await _discoveryCrawler.CrawlAsync(stoppingToken);
                _logger.LogInformation(
                    "Discovery complete: {Pages} pages processed, {Discovered} settlements in queue",
                    result.PagesProcessed, result.TotalSettlementsDiscovered);
            }

            if (_cliOptions.Mode is "all" or "download")
            {
                _logger.LogInformation("Executing Settlement CSV Ingestion phase...");

                // Deferred drain registration: consumer runs unbounded during active ingestion
                using var drainCts = new CancellationTokenSource();
                using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

                // Arm the deferred drain timeout immediately if host shutdown is initiated
                using var shutdownRegistration = stoppingToken.Register(() => drainCts.CancelAfter(_drainTimeout));

                // Storage engine listens to drain token without initial timeout
                var storageTask = Task.Run(() => _storageEngine.ProcessChannelAsync(drainCts.Token));

                // Ingestion bounded by linked producer token
                var ingestionTask = _ingestionService.IngestAsync(producerCts.Token);

                var completedTask = await Task.WhenAny(ingestionTask, storageTask);

                if (completedTask == storageTask && !ingestionTask.IsCompleted)
                {
                    // Fail-fast fault propagation: consumer terminated while ingestion is still active
                    _logger.LogError("Storage consumer terminated prematurely while ingestion was active.");

                    // Immediately cancel producer token
                    producerCts.Cancel();

                    // Immediately complete channel writer to unblock awaiting EnqueueAsync calls
                    var consumerEx = storageTask.Exception?.GetBaseException();
                    _channel.Complete(consumerEx ?? new InvalidOperationException("Storage consumer terminated prematurely."));

                    try
                    {
                        await ingestionTask;
                    }
                    catch
                    {
                        // Ingestion aborted due to consumer termination; ignore secondary producer exceptions
                    }

                    if (storageTask.IsFaulted && storageTask.Exception != null)
                    {
                        throw storageTask.Exception.GetBaseException();
                    }

                    if (storageTask.IsCanceled)
                    {
                        throw new InvalidOperationException("Storage consumer was canceled prematurely while ingestion was active.");
                    }

                    throw new InvalidOperationException("Storage consumer terminated prematurely while ingestion was active.");
                }

                Exception? ingestionException = null;
                try
                {
                    var ingestionResult = await ingestionTask;
                    _logger.LogInformation(
                        "Ingestion network phase finished: {Total} processed ({Succeeded} succeeded, {Failed} failed).",
                        ingestionResult.TotalProcessed, ingestionResult.Succeeded, ingestionResult.Failed);
                }
                catch (Exception ex)
                {
                    ingestionException = ex;
                }
                finally
                {
                    // Ingestion ended: close channel writer so consumer stream can finish
                    _channel.Complete();
                    _logger.LogInformation("Channel writer marked complete. Draining pending SQLite write transactions...");

                    // Arm deferred drain timeout exclusively during shutdown/drain
                    drainCts.CancelAfter(_drainTimeout);

                    try
                    {
                        await storageTask;
                    }
                    catch (OperationCanceledException) when (drainCts.IsCancellationRequested)
                    {
                        // Drain timed out
                    }

                    _progressCoordinator?.Complete();

                    if (_channel.Count > 0 || drainCts.IsCancellationRequested)
                    {
                        _logger.LogError(
                            "Channel drain timed out with {Count} item(s) remaining in buffer.",
                            _channel.Count);
                    }
                    else
                    {
                        _logger.LogInformation("All pending SQLite write transactions committed successfully.");
                    }
                }

                if (ingestionException != null)
                {
                    throw ingestionException;
                }
            }

            var finalPending = await _repository.GetCountByStatusAsync("pending", stoppingToken);
            var finalCompleted = await _repository.GetCountByStatusAsync("completed", stoppingToken);
            var finalFailed = await _repository.GetCountByStatusAsync("failed", stoppingToken);
            var finalTotal = await _repository.GetCountAsync(stoppingToken);
            _logger.LogInformation(
                "Summary: {Total} total settlements in queue ({Pending} pending, {Completed} completed, {Failed} failed).",
                finalTotal, finalPending, finalCompleted, finalFailed);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Worker execution canceled gracefully.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Fatal error occurred during worker execution.");
            Environment.ExitCode = 1;
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }
}