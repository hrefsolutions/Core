using Href.Core.Api.Modules.Notifications.Email;
using Href.Core.Api.Modules.Notifications.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Href.Core.Tests.Modules.Notifications.Worker;

public sealed class NotificationWorkerStartupCheckTests
{
    [Theory]
    [InlineData(10, 20, 30, false)]
    [InlineData(10, 21, 30, true)]
    [InlineData(11, 20, 30, false)]
    [InlineData(10, 10, 30, false)]
    [InlineData(1, 1, 61, true)]
    public async Task StartAsync_WarnsOnlyWhenStuckAfterMinutesIsLessThanTheBatchSendTime(
        int stuckAfterMinutes,
        int batchSize,
        int timeoutSeconds,
        bool expectWarning)
    {
        var sink = await RunCheckAsync(
            new NotificationWorkerOptions { StuckAfterMinutes = stuckAfterMinutes, BatchSize = batchSize },
            timeoutSeconds);

        Assert.Equal(expectWarning, sink.Events.Any(logEvent => logEvent.Level == LogEventLevel.Warning));
    }

    [Fact]
    public async Task StartAsync_WhenWorkerIsDisabled_DoesNotWarn()
    {
        var sink = await RunCheckAsync(new NotificationWorkerOptions { Enabled = false, StuckAfterMinutes = 1, BatchSize = 500 }, 300);

        Assert.Empty(sink.Events);
    }

    private static async Task<CapturingLogSink> RunCheckAsync(NotificationWorkerOptions workerOptions, int timeoutSeconds)
    {
        var sink = new CapturingLogSink();
        using var serilog = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        var check = new NotificationWorkerStartupCheck(
            Options.Create(workerOptions),
            Options.Create(new SmtpOptions { TimeoutSeconds = timeoutSeconds }),
            new SerilogLoggerFactory(serilog).CreateLogger<NotificationWorkerStartupCheck>());

        await check.StartAsync(TestContext.Current.CancellationToken);
        await check.StopAsync(TestContext.Current.CancellationToken);

        return sink;
    }
}
