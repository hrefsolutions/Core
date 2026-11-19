using Href.Core.Api.Modules.Notifications.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Href.Core.Tests.Modules.Notifications.Worker;

public sealed class NotificationWorkerOptionsTests
{
    private static NotificationWorkerOptions Resolve(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddNotificationWorker(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<NotificationWorkerOptions>>().Value;
    }

    [Fact]
    public void WithoutConfiguration_UsesDefaults()
    {
        var options = Resolve([]);

        Assert.True(options.Enabled);
        Assert.Equal(5, options.PollIntervalSeconds);
        Assert.Equal(20, options.BatchSize);
        Assert.Equal(10, options.StuckAfterMinutes);
    }

    [Fact]
    public void BindsEveryValueFromTheWorkerSection()
    {
        var options = Resolve(new Dictionary<string, string?>
        {
            ["Notifications:Worker:Enabled"] = "false",
            ["Notifications:Worker:PollIntervalSeconds"] = "30",
            ["Notifications:Worker:BatchSize"] = "50",
            ["Notifications:Worker:StuckAfterMinutes"] = "15",
        });

        Assert.False(options.Enabled);
        Assert.Equal(30, options.PollIntervalSeconds);
        Assert.Equal(50, options.BatchSize);
        Assert.Equal(15, options.StuckAfterMinutes);
    }

    [Theory]
    [InlineData("PollIntervalSeconds", "1")]
    [InlineData("PollIntervalSeconds", "3600")]
    [InlineData("BatchSize", "1")]
    [InlineData("BatchSize", "500")]
    [InlineData("StuckAfterMinutes", "1")]
    [InlineData("StuckAfterMinutes", "1440")]
    public void BoundaryValues_AreValid(string key, string value)
    {
        var options = Resolve(new Dictionary<string, string?> { [$"Notifications:Worker:{key}"] = value });

        Assert.NotNull(options);
    }

    [Theory]
    [InlineData("PollIntervalSeconds", "0")]
    [InlineData("PollIntervalSeconds", "-1")]
    [InlineData("PollIntervalSeconds", "3601")]
    [InlineData("BatchSize", "0")]
    [InlineData("BatchSize", "501")]
    [InlineData("StuckAfterMinutes", "0")]
    [InlineData("StuckAfterMinutes", "1441")]
    public void OutOfRangeValues_AreInvalid(string key, string value)
    {
        var values = new Dictionary<string, string?> { [$"Notifications:Worker:{key}"] = value };

        Assert.Throws<OptionsValidationException>(() => Resolve(values));
    }

    [Theory]
    [InlineData("Enabled", "maybe")]
    [InlineData("BatchSize", "ten")]
    public void UnparsableValues_AreRejected(string key, string value)
    {
        var values = new Dictionary<string, string?> { [$"Notifications:Worker:{key}"] = value };

        Assert.Throws<InvalidOperationException>(() => Resolve(values));
    }

    [Fact]
    public async Task HostStart_WithInvalidWorkerOptions_ThrowsOptionsValidationException()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifications:Worker:BatchSize"] = "0",
        });
        builder.Services.AddNotificationWorker(builder.Configuration);
        using var host = builder.Build();

        var exception = await Record.ExceptionAsync(() => host.StartAsync(TestContext.Current.CancellationToken));

        var validationException = Assert.IsType<OptionsValidationException>(exception);
        Assert.Equal(typeof(NotificationWorkerOptions), validationException.OptionsType);
    }

    [Fact]
    public async Task HostStart_WithUnparsableEnabled_Fails()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifications:Worker:Enabled"] = "maybe",
        });
        builder.Services.AddNotificationWorker(builder.Configuration);
        using var host = builder.Build();

        var exception = await Record.ExceptionAsync(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.IsType<InvalidOperationException>(exception);
    }
}
