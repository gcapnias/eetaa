using System;
using System.Linq;
using System.Threading.Tasks;

using Eetaa.Worker.Ingestion;

using Microsoft.Extensions.Logging;

using Xunit;

namespace Eetaa.Worker.Tests.Ingestion;

public class IngestionProgressCoordinatorTests
{
    private readonly TestLogger<IngestionProgressCoordinator> _logger;

    public IngestionProgressCoordinatorTests()
    {
        _logger = new TestLogger<IngestionProgressCoordinator>();
    }

    [Fact]
    public void Start_ZeroItems_DoesNotThrowAndReportsNothingInitially()
    {
        using var coordinator = new IngestionProgressCoordinator(_logger, reportInterval: TimeSpan.FromSeconds(15), reportItemCadence: 100);
        coordinator.Start(0, () => 0);

        Assert.Equal(0, coordinator.Total);
        Assert.Equal(0, coordinator.Persisted);
        Assert.Equal(0, coordinator.Failed);

        coordinator.Complete();

        // On complete with 0 items, should it log or gracefully do nothing?
        // Let's verify no error and clean completion.
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public void RecordPersisted_TriggersLog_EveryCadenceItems()
    {
        using var coordinator = new IngestionProgressCoordinator(_logger, reportInterval: TimeSpan.FromMinutes(10), reportItemCadence: 100);
        coordinator.Start(250, () => 5);

        // Record 99 items
        for (var i = 0; i < 99; i++)
        {
            coordinator.RecordPersisted();
        }

        Assert.Empty(_logger.Entries);

        // 100th item triggers log
        coordinator.RecordPersisted();

        Assert.Single(_logger.Entries);
        var entry = _logger.Entries[0];
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("Progress: 100/250 settlements (40.0%)", entry.Message);
        Assert.Contains("Failed: 0", entry.Message);
        Assert.Contains("Buffer: 5", entry.Message);
    }

    [Fact]
    public void RecordFailed_CountsTowardCadenceAndFailureMetrics()
    {
        using var coordinator = new IngestionProgressCoordinator(_logger, reportInterval: TimeSpan.FromMinutes(10), reportItemCadence: 50);
        coordinator.Start(100, () => 2);

        for (var i = 0; i < 40; i++)
        {
            coordinator.RecordPersisted();
        }
        for (var i = 0; i < 10; i++)
        {
            coordinator.RecordFailed();
        }

        Assert.Single(_logger.Entries);
        var entry = _logger.Entries[0];
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("Progress: 40/100 settlements (40.0%)", entry.Message);
        Assert.Contains("Failed: 10", entry.Message);
        Assert.Contains("Buffer: 2", entry.Message);
    }

    [Fact]
    public void Complete_EmitsFinalSummaryLog_WhenTotalGreaterThanZero()
    {
        using var coordinator = new IngestionProgressCoordinator(_logger, reportInterval: TimeSpan.FromMinutes(10), reportItemCadence: 100);
        coordinator.Start(50, () => 0);

        for (var i = 0; i < 48; i++)
        {
            coordinator.RecordPersisted();
        }
        coordinator.RecordFailed();
        coordinator.RecordFailed();

        Assert.Empty(_logger.Entries); // 50 < cadence 100, so no cadence logs yet

        coordinator.Complete();

        Assert.Single(_logger.Entries);
        var entry = _logger.Entries[0];
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("Progress: 48/50 settlements (96.0%)", entry.Message);
        Assert.Contains("Failed: 2", entry.Message);
        Assert.Contains("Buffer: 0", entry.Message);
    }

    [Fact]
    public async Task Timer_EmitsProgressLog_Periodically()
    {
        using var coordinator = new IngestionProgressCoordinator(_logger, reportInterval: TimeSpan.FromMilliseconds(50), reportItemCadence: 100);
        coordinator.Start(200, () => 3);

        coordinator.RecordPersisted();

        // Wait for timer to tick at least once
        await Task.Delay(120);

        Assert.NotEmpty(_logger.Entries);
        var entry = _logger.Entries[0];
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("Progress: 1/200 settlements (0.5%)", entry.Message);
        Assert.Contains("Buffer: 3", entry.Message);
    }

    [Fact]
    public void Complete_WhenCadenceAlreadyReportedFinalCount_DoesNotEmitDuplicateLog()
    {
        using var coordinator = new IngestionProgressCoordinator(_logger, reportInterval: TimeSpan.FromMinutes(10), reportItemCadence: 100);
        coordinator.Start(100, () => 0);

        for (var i = 0; i < 100; i++)
        {
            coordinator.RecordPersisted();
        }

        // At 100 items, the cadence trigger emits 1 log
        Assert.Single(_logger.Entries);

        coordinator.Complete();

        // Should not duplicate the 100/100 log
        Assert.Single(_logger.Entries);
    }

    [Fact]
    public void Complete_WhenCancelledOrPartial_EmitsFinalSummaryLog()
    {
        using var coordinator = new IngestionProgressCoordinator(_logger, reportInterval: TimeSpan.FromMinutes(10), reportItemCadence: 100);
        coordinator.Start(500, () => 15);

        for (var i = 0; i < 35; i++)
        {
            coordinator.RecordPersisted();
        }
        for (var i = 0; i < 5; i++)
        {
            coordinator.RecordFailed();
        }

        Assert.Empty(_logger.Entries);

        coordinator.Complete();

        Assert.Single(_logger.Entries);
        var entry = _logger.Entries[0];
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("Progress: 35/500 settlements (7.0%)", entry.Message);
        Assert.Contains("Failed: 5", entry.Message);
        Assert.Contains("Buffer: 15", entry.Message);
    }
}