using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace Href.Core.Tests;

/// <summary>
/// Serilog sink that keeps every emitted <see cref="LogEvent"/> in memory for assertions.
/// </summary>
public sealed class CapturingLogSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public IReadOnlyCollection<LogEvent> Events => _events;

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

    /// <summary>
    /// Waits until an event matching <paramref name="predicate"/> is emitted, because some events
    /// (such as the request completion log) are written after the HTTP response reaches the client.
    /// </summary>
    public async Task<LogEvent> WaitForAsync(Func<LogEvent, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            var match = _events.FirstOrDefault(predicate);

            if (match is not null)
            {
                return match;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("No matching log event was emitted.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }
}
