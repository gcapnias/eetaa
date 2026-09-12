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
    private readonly ISqliteLocalAuthorityQueueRepository? _authorityRepository;
    private readonly ILocalAuthorityDiscoveryCrawler? _authorityCrawler;

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
        TimeSpan? drainTimeout = null,
        ISqliteLocalAuthorityQueueRepository? authorityRepository = null,
        ILocalAuthorityDiscoveryCrawler? authorityCrawler = null)
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
        _authorityRepository = authorityRepository;
        _authorityCrawler = authorityCrawler;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Worker starting in mode: {Mode}, entity: {Entity}, db: {Db}", _cliOptions.Mode, _cliOptions.Entity, _cliOptions.DatabasePath);

            var runSettlements = _cliOptions.Entity is "all" or "settlements";
            var runAuthorities = _cliOptions.Entity is "all" or "authorities";

            // 1. Initialize SQLite schema and PRAGMAs
            await _repository.InitializeDatabaseAsync(stoppingToken);
            if (_authorityRepository != null)
            {
                await _authorityRepository.InitializeDatabaseAsync(stoppingToken);
            }
            _logger.LogInformation("SQLite database initialized at {DbPath}", _cliOptions.DatabasePath);

            // 2. In-flight recovery: reset any 'processing' items left behind by previous crashes
            if (runSettlements)
            {
                var resetCount = await _repository.ResetInFlightProcessingAsync(stoppingToken);
                if (resetCount > 0)
                {
                    _logger.LogInformation("Reset {ResetCount} orphaned in-flight 'processing' settlement(s) to 'pending'", resetCount);
                }

                var initialCount = await _repository.GetCountAsync(stoppingToken);
                _logger.LogInformation("Current settlement_queue record count: {Count}", initialCount);
            }

            if (runAuthorities && _authorityRepository != null)
            {
                var resetAuthCount = await _authorityRepository.ResetInFlightProcessingAsync(stoppingToken);
                if (resetAuthCount > 0)
                {
                    _logger.LogInformation("Reset {ResetCount} orphaned in-flight 'processing' local authority(s) to 'pending'", resetAuthCount);
                }

                var initialAuthCount = await _authorityRepository.GetCountAsync(stoppingToken);
                _logger.LogInformation("Current local_authority_queue record count: {Count}", initialAuthCount);
            }

            // 3. Execute according to mode
            if (_cliOptions.Mode is "all" or "discover")
            {
                if (runSettlements)
                {
                    _logger.LogInformation("Executing Settlement Index Discovery phase...");
                    var result = await _discoveryCrawler.CrawlAsync(stoppingToken);
                    _logger.LogInformation(
                        "Settlement discovery complete: {Pages} pages processed, {Discovered} settlements in queue",
                        result.PagesProcessed, result.TotalSettlementsDiscovered);
                }

                if (runAuthorities && _authorityCrawler != null)
                {
                    _logger.LogInformation("Executing Local Authority Index Discovery phase...");
                    var result = await _authorityCrawler.CrawlAsync(stoppingToken);
                    _logger.LogInformation(
                        "Local authority discovery complete: {Pages} pages processed, {Discovered} local authorities in queue",
                        result.PagesProcessed, result.TotalSettlementsDiscovered);
                }
            }

            if (_cliOptions.Mode is "all" or "download")
            {
                if (runSettlements)
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
            }

            if (runSettlements)
            {
                var finalPending = await _repository.GetCountByStatusAsync("pending", stoppingToken);
                var finalCompleted = await _repository.GetCountByStatusAsync("completed", stoppingToken);
                var finalFailed = await _repository.GetCountByStatusAsync("failed", stoppingToken);
                var finalTotal = await _repository.GetCountAsync(stoppingToken);
                _logger.LogInformation(
                    "Settlement summary: {Total} total settlements in queue ({Pending} pending, {Completed} completed, {Failed} failed).",
                    finalTotal, finalPending, finalCompleted, finalFailed);
            }

            if (runAuthorities && _authorityRepository != null)
            {
                var finalAuthPending = await _authorityRepository.GetCountByStatusAsync("pending", stoppingToken);
                var finalAuthCompleted = await _authorityRepository.GetCountByStatusAsync("completed", stoppingToken);
                var finalAuthFailed = await _authorityRepository.GetCountByStatusAsync("failed", stoppingToken);
                var finalAuthTotal = await _authorityRepository.GetCountAsync(stoppingToken);
                _logger.LogInformation(
                    "Local authority summary: {Total} total local authorities in queue ({Pending} pending, {Completed} completed, {Failed} failed).",
                    finalAuthTotal, finalAuthPending, finalAuthCompleted, finalAuthFailed);
            }
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