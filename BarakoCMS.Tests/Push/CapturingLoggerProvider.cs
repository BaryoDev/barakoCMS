using Microsoft.Extensions.Logging;

namespace BarakoCMS.Tests.Push;

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private static readonly object Gate = new();
    private static readonly List<string> Lines = new();

    public static IReadOnlyList<string> Captured
    {
        get { lock (Gate) return Lines.ToList(); }
    }

    public static void Clear()
    {
        lock (Gate) Lines.Clear();
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger();
    public void Dispose() { }

    public sealed class Typed<T>(ILoggerProvider provider) : ILogger<T>
    {
        private readonly ILogger _inner = provider.CreateLogger(typeof(T).FullName!);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private sealed class CapturingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            if (exception is not null) line += " " + exception;
            lock (Gate) Lines.Add(line);
        }
    }
}
