using Href.Core.Api.Modules.Notifications.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Href.Core.Tests.Modules.Notifications.Worker;

/// <summary>
/// Worker loop tests with a <see cref="FakeTimeProvider"/>. The scripted dispatcher records the fake time of each call,
/// so the waits are asserted on fake time: the tests advance the clock in small steps until the expected call happens,
/// and never depend on how long a real sleep lasts.
/// </summary>
public sealed class NotificationBackgroundWorkerTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(500);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WhenDisabled_ReturnsImmediatelyWithoutCreatingScopes()
    {
        await using var harness = new Harness(new NotificationWorkerOptions { Enabled = false });

        await harness.Worker.StartAsync(Ct);

        Assert.NotNull(harness.Worker.ExecuteTask);
        await harness.Worker.ExecuteTask.WaitAsync(WaitTimeout, Ct);
        Assert.True(harness.Worker.ExecuteTask.IsCompletedSuccessfully);
        Assert.Equal(0, harness.ScopesCreated);
        Assert.Equal(0, harness.Dispatcher.RecoverCalls);
        Assert.Equal(0, harness.Dispatcher.DispatchCalls);
    }

    [Fact]
    public async Task WhenBatchIsNotFull_WaitsThePollIntervalOnTheTimeProvider()
    {
        await using var harness = new Harness(new NotificationWorkerOptions { PollIntervalSeconds = 5, BatchSize = 3 });
        harness.Dispatcher.OnDispatch = _ => new DispatchResult(2, 0);

        await harness.Worker.StartAsync(Ct);
        await AdvanceUntilAsync(harness.Clock, () => harness.Dispatcher.DispatchCalls >= 2);

        var times = harness.Dispatcher.DispatchTimes;
        Assert.True(times[1] - times[0] >= TimeSpan.FromSeconds(5), $"Second call after {times[1] - times[0]}");
        Assert.True(harness.Dispatcher.RecoverCalls >= 2);
        Assert.True(harness.ScopesCreated >= 2);
    }

    [Fact]
    public async Task WhenBatchIsFull_StartsTheNextIterationWithoutWaiting()
    {
        await using var harness = new Harness(new NotificationWorkerOptions { PollIntervalSeconds = 60, BatchSize = 5 });
        harness.Dispatcher.OnDispatch = call => call <= 3 ? new DispatchResult(5, 0) : new DispatchResult(0, 0);

        await harness.Worker.StartAsync(Ct);
        await WaitUntilAsync(() => harness.Dispatcher.DispatchCalls >= 4);

        Assert.All(harness.Dispatcher.DispatchTimes.Take(4), time => Assert.Equal(harness.Start, time));
        Assert.Equal(4, harness.Dispatcher.DispatchCalls);
    }

    [Fact]
    public async Task WhenEveryClaimedNotificationHitAnUnexpectedError_WaitsThePollIntervalEvenIfTheBatchWasFull()
    {
        await using var harness = new Harness(new NotificationWorkerOptions { PollIntervalSeconds = 5, BatchSize = 5 });
        harness.Dispatcher.OnDispatch = call => call == 1 ? new DispatchResult(5, 5) : new DispatchResult(0, 0);

        await harness.Worker.StartAsync(Ct);
        await AdvanceUntilAsync(harness.Clock, () => harness.Dispatcher.DispatchCalls >= 2);

        var times = harness.Dispatcher.DispatchTimes;
        Assert.True(times[1] - times[0] >= TimeSpan.FromSeconds(5), $"Second call after {times[1] - times[0]}");
    }

    [Fact]
    public async Task WhenSomeClaimedNotificationsHitAnUnexpectedError_AFullBatchStillLoopsImmediately()
    {
        await using var harness = new Harness(new NotificationWorkerOptions { PollIntervalSeconds = 60, BatchSize = 5 });
        harness.Dispatcher.OnDispatch = call => call == 1 ? new DispatchResult(5, 4) : new DispatchResult(0, 0);

        await harness.Worker.StartAsync(Ct);
        await WaitUntilAsync(() => harness.Dispatcher.DispatchCalls >= 2);

        Assert.All(harness.Dispatcher.DispatchTimes.Take(2), time => Assert.Equal(harness.Start, time));
    }

    [Fact]
    public async Task WhenAnIterationThrows_LogsTheErrorAndKeepsPolling()
    {
        await using var harness = new Harness(new NotificationWorkerOptions { PollIntervalSeconds = 5 });
        harness.Dispatcher.OnDispatch = call => call == 1 ? throw new InvalidOperationException("Database unavailable") : new DispatchResult(0, 0);

        await harness.Worker.StartAsync(Ct);
        var error = await harness.Sink.WaitForAsync(
            logEvent => logEvent.Level == LogEventLevel.Error && logEvent.Exception is InvalidOperationException,
            WaitTimeout,
            Ct);
        await AdvanceUntilAsync(harness.Clock, () => harness.Dispatcher.DispatchCalls >= 2);

        Assert.Contains("iteration failed", error.MessageTemplate.Text, StringComparison.Ordinal);
        Assert.NotNull(harness.Worker.ExecuteTask);
        Assert.False(harness.Worker.ExecuteTask.IsCompleted);
    }

    [Fact]
    public async Task WhenRecoveryThrows_LogsTheErrorAndStillDispatches()
    {
        await using var harness = new Harness(new NotificationWorkerOptions { PollIntervalSeconds = 5 });
        harness.Dispatcher.OnRecover = () => throw new InvalidOperationException("Recovery failed");

        await harness.Worker.StartAsync(Ct);
        await WaitUntilAsync(() => harness.Dispatcher.DispatchCalls >= 1);

        Assert.Equal(1, harness.Dispatcher.RecoverCalls);
        Assert.Contains(harness.Sink.Events, logEvent =>
            logEvent.Level == LogEventLevel.Error
            && logEvent.MessageTemplate.Text.Contains("Recovery of stuck notifications failed", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Sink.Events, logEvent => logEvent.MessageTemplate.Text.Contains("iteration failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopAsync_EndsTheLoopWhileItWaits()
    {
        await using var harness = new Harness(new NotificationWorkerOptions { PollIntervalSeconds = 3600 });

        await harness.Worker.StartAsync(Ct);
        await WaitUntilAsync(() => harness.Dispatcher.DispatchCalls == 1);
        await harness.Worker.StopAsync(Ct);

        Assert.NotNull(harness.Worker.ExecuteTask);
        await harness.Worker.ExecuteTask.WaitAsync(WaitTimeout, Ct);
        Assert.True(harness.Worker.ExecuteTask.IsCompletedSuccessfully);
        Assert.Equal(1, harness.Dispatcher.DispatchCalls);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;

        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), Ct);
        }
    }

    private static async Task AdvanceUntilAsync(FakeTimeProvider clock, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;

        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            clock.Advance(Step);
            await Task.Delay(TimeSpan.FromMilliseconds(10), Ct);
        }
    }

    private sealed class ScriptedDispatcher(TimeProvider clock) : INotificationDispatcher
    {
        private readonly Lock _gate = new();
        private readonly List<DateTimeOffset> _dispatchTimes = [];
        private int _dispatchCalls;
        private int _recoverCalls;

        public Func<int, DispatchResult> OnDispatch { get; set; } = _ => new DispatchResult(0, 0);

        public Action? OnRecover { get; set; }

        public int DispatchCalls => Volatile.Read(ref _dispatchCalls);

        public int RecoverCalls => Volatile.Read(ref _recoverCalls);

        public IReadOnlyList<DateTimeOffset> DispatchTimes
        {
            get
            {
                lock (_gate)
                {
                    return _dispatchTimes.ToList();
                }
            }
        }

        public Task<int> RecoverStuckAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _recoverCalls);
            OnRecover?.Invoke();
            return Task.FromResult(0);
        }

        public Task<DispatchResult> DispatchBatchAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _dispatchTimes.Add(clock.GetUtcNow());
            }

            var call = Interlocked.Increment(ref _dispatchCalls);
            return Task.FromResult(OnDispatch(call));
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly Serilog.Core.Logger _serilog;
        private int _scopesCreated;

        public Harness(NotificationWorkerOptions options)
        {
            Clock = new FakeTimeProvider(Start);
            Dispatcher = new ScriptedDispatcher(Clock);
            var services = new ServiceCollection();
            services.AddScoped<INotificationDispatcher>(_ =>
            {
                Interlocked.Increment(ref _scopesCreated);
                return Dispatcher;
            });
            _provider = services.BuildServiceProvider();
            _serilog = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(Sink).CreateLogger();
            Worker = new NotificationBackgroundWorker(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(options),
                Clock,
                new SerilogLoggerFactory(_serilog).CreateLogger<NotificationBackgroundWorker>());
        }

        public DateTimeOffset Start { get; } = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

        public FakeTimeProvider Clock { get; }

        public ScriptedDispatcher Dispatcher { get; }

        public CapturingLogSink Sink { get; } = new();

        public NotificationBackgroundWorker Worker { get; }

        public int ScopesCreated => Volatile.Read(ref _scopesCreated);

        public async ValueTask DisposeAsync()
        {
            await Worker.StopAsync(CancellationToken.None);
            Worker.Dispose();
            await _provider.DisposeAsync();
            _serilog.Dispose();
        }
    }
}
