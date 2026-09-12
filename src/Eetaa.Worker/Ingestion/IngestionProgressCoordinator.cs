using System.Diagnostics;

using Microsoft.Extensions.Logging;

namespace Eetaa.Worker.Ingestion;

public class IngestionProgressCoordinator : IIngestionProgressCoordinator
{
    private readonly ILogger<IngestionProgressCoordinator> _logger;
    private readonly TimeSpan _reportInterval;
    private readonly int _reportItemCadence;

    private readonly object _syncLock = new();
    private Timer? _timer;
    private Stopwatch? _stopwatch;
    private Func<int>? _getBufferCount;

    private int _total;
    private int _downloaded;
    private int _persisted;
    private int _failed;
    private int _lastReportedCompletedCount;
    private string _entityLabel;
    private bool _isStarted;
    private bool _isCompleted;

    public int Total => Volatile.Read(ref _total);
    public int Downloaded => Volatile.Read(ref _downloaded);
    public int Persisted => Volatile.Read(ref _persisted);
    public int Failed => Volatile.Read(ref _failed);
    public string EntityLabel
    {
        get
        {
            lock (_syncLock)
            {
                return _entityLabel;
            }
        }
    }

    public IngestionProgressCoordinator(
        ILogger<IngestionProgressCoordinator> logger,
        TimeSpan? reportInterval = null,
        int reportItemCadence = 100,
        string entityLabel = "settlement")
    {
        _logger = logger;
        _reportInterval = reportInterval ?? TimeSpan.FromSeconds(15);
        _reportItemCadence = reportItemCadence > 0 ? reportItemCadence : 100;
        _entityLabel = string.IsNullOrWhiteSpace(entityLabel) ? "settlement" : entityLabel;
    }

    public void Start(int totalCount, Func<int> getBufferCount, string? entityLabel = null)
    {
        lock (_syncLock)
        {
            if (_isStarted)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(entityLabel))
            {
                _entityLabel = entityLabel;
            }

            _isStarted = true;
            _total = totalCount;
            _getBufferCount = getBufferCount;
            _downloaded = 0;
            _persisted = 0;
            _failed = 0;
            _lastReportedCompletedCount = 0;
            _isCompleted = false;

            if (totalCount > 0)
            {
                _stopwatch = Stopwatch.StartNew();
                _timer = new Timer(OnTimerTick, null, _reportInterval, _reportInterval);
            }
        }
    }

    public void RecordDownloaded()
    {
        Interlocked.Increment(ref _downloaded);
    }

    public void RecordPersisted()
    {
        Interlocked.Increment(ref _persisted);
        CheckCadenceReporting();
    }

    public void RecordFailed()
    {
        Interlocked.Increment(ref _failed);
        CheckCadenceReporting();
    }

    private void CheckCadenceReporting()
    {
        var total = Volatile.Read(ref _total);
        if (total == 0)
        {
            return;
        }

        lock (_syncLock)
        {
            if (!_isStarted || _isCompleted)
            {
                return;
            }

            var currentCompleted = _persisted + _failed;
            if (currentCompleted - _lastReportedCompletedCount >= _reportItemCadence)
            {
                _lastReportedCompletedCount = currentCompleted;
                EmitProgressLog();
            }
        }
    }

    private void OnTimerTick(object? state)
    {
        lock (_syncLock)
        {
            if (!_isStarted || _isCompleted || _total == 0)
            {
                return;
            }

            var currentCompleted = _persisted + _failed;
            // Report while work is active (work is active until all items are completed)
            if (currentCompleted < _total)
            {
                _lastReportedCompletedCount = currentCompleted;
                EmitProgressLog();
            }
        }
    }

    public void Complete()
    {
        lock (_syncLock)
        {
            if (!_isStarted || _isCompleted)
            {
                return;
            }

            _isCompleted = true;

            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }

            var currentCompleted = _persisted + _failed;
            // Emit final summary if total > 0 and this count hasn't just been reported by cadence trigger
            if (_total > 0 && currentCompleted != _lastReportedCompletedCount)
            {
                _lastReportedCompletedCount = currentCompleted;
                EmitProgressLog();
            }

            _stopwatch?.Stop();
        }
    }

    private void EmitProgressLog()
    {
        var total = _total;
        var persisted = _persisted;
        var failed = _failed;
        var completed = persisted + failed;

        var percent = total > 0 ? (double)persisted / total * 100.0 : 0.0;

        var elapsedSeconds = _stopwatch?.Elapsed.TotalSeconds ?? 0.0;
        var rate = elapsedSeconds > 0.05 ? completed / elapsedSeconds : 0.0;

        var remainingItems = Math.Max(0, total - completed);
        var eta = (rate > 0.001)
            ? TimeSpan.FromSeconds(remainingItems / rate)
            : TimeSpan.Zero;

        var bufferCount = _getBufferCount?.Invoke() ?? 0;
        var entityPlural = GetPluralLabel();

        _logger.LogInformation(
            "Progress: {Persisted}/{Total} {Entity} ({Percent:F1}%) | {Rate:F1} item/s | ETA: {Eta:hh\\:mm\\:ss} | Failed: {Failed} | Buffer: {BufferCount}",
            persisted,
            total,
            entityPlural,
            percent,
            rate,
            eta,
            failed,
            bufferCount);
    }

    private string GetPluralLabel()
    {
        var label = _entityLabel;
        if (string.Equals(label, "local authority", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(label, "local authorities", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(label, "authorities", StringComparison.OrdinalIgnoreCase))
        {
            return "local authorities";
        }

        if (string.Equals(label, "settlement", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(label, "settlements", StringComparison.OrdinalIgnoreCase))
        {
            return "settlements";
        }

        return label;
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            _isCompleted = true;
            _timer?.Dispose();
            _timer = null;
            _stopwatch?.Stop();
        }
    }
}