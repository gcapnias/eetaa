using Eetaa.Worker.Configuration;
using Eetaa.Worker.Data;
using Eetaa.Worker.Discovery;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Eetaa.Worker;

public sealed class Worker : BackgroundService
{
    private readonly ISqliteQueueRepository _repository;
    private readonly IIndexDiscoveryCrawler _discoveryCrawler;
    private readonly WorkerCliOptions _cliOptions;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<Worker> _logger;

    public Worker(
        ISqliteQueueRepository repository,
        IIndexDiscoveryCrawler discoveryCrawler,
        WorkerCliOptions cliOptions,
        IHostApplicationLifetime lifetime,
        ILogger<Worker> logger)
    {
        _repository = repository;
        _discoveryCrawler = discoveryCrawler;
        _cliOptions = cliOptions;
        _lifetime = lifetime;
        _logger = logger;
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
                _logger.LogInformation("Download phase will be handled in subsequent phase.");
            }

            var finalPending = await _repository.GetCountByStatusAsync("pending", stoppingToken);
            var finalTotal = await _repository.GetCountAsync(stoppingToken);
            _logger.LogInformation("Summary: {Total} total settlements in queue ({Pending} pending).", finalTotal, finalPending);
        }
        catch (OperationCanceledException)
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
