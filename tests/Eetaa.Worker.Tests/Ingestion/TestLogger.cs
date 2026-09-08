using System;
using System.Collections.Generic;

using Microsoft.Extensions.Logging;

namespace Eetaa.Worker.Tests.Ingestion;

public sealed class TestLogger<T> : ILogger<T>
{
    private readonly List<TestLogEntry> _entries = new();
    private readonly object _lock = new();

    public IReadOnlyList<TestLogEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToArray();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        lock (_lock)
        {
            _entries.Add(new TestLogEntry(logLevel, message, exception));
        }
    }
}

public sealed record TestLogEntry(LogLevel Level, string Message, Exception? Exception);