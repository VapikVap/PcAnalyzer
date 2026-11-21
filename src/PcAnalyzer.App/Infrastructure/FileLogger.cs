using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PcAnalyzer.App.Infrastructure;

/// <summary>Minimal async file logger: one file per day, old files removed after 14 days.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly BlockingCollection<string> _queue = new(10_000);
    private readonly Thread _writer;
    private readonly string _dir;

    public FileLoggerProvider(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        Cleanup();
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "FileLogger" };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Enqueue(string line) => _queue.TryAdd(line);

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                File.AppendAllText(Path.Combine(_dir, $"app-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // logging must never crash the app
            }
        }
    }

    private void Cleanup()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_dir, "app-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f);
        }
        catch { /* ignore */ }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(1000);
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        private readonly string _short = category[(category.LastIndexOf('.') + 1)..];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ').Append(level.ToString()[..4].ToUpperInvariant()).Append(' ')
              .Append(_short).Append(": ").Append(formatter(state, ex));
            if (ex != null) sb.AppendLine().Append(ex);
            owner.Enqueue(sb.ToString());
        }
    }
}
