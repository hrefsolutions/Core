using Href.Core.Api.Modules.Notifications.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Href.Core.Tests.Modules.Notifications.Email;

public sealed class SmtpStartupCheckTests
{
    private static async Task<List<string>> StartAndCollectWarningsAsync(string secureSocket, string? username, string? password)
    {
        var provider = new ListLoggerProvider();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Smtp:Host"] = "smtp.example.test",
            ["Smtp:Port"] = "465",
            ["Smtp:SecureSocket"] = secureSocket,
            ["Smtp:Username"] = username,
            ["Smtp:Password"] = password,
            ["Smtp:FromEmail"] = "href.notifier@gmail.com",
            ["Smtp:FromName"] = "Href",
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(provider);
        builder.Services.AddSmtpEmail(builder.Configuration);
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        return provider.Warnings;
    }

    [Fact]
    public async Task Start_WithSecureSocketAndNoCredentials_LogsWarningNamingTheHost()
    {
        var warnings = await StartAndCollectWarningsAsync("SslOnConnect", null, null);

        var warning = Assert.Single(warnings);
        Assert.Contains("smtp.example.test", warning);
    }

    [Fact]
    public async Task Start_WithCredentials_LogsNoWarning()
    {
        var warnings = await StartAndCollectWarningsAsync("SslOnConnect", "href.notifier@gmail.com", "placeholder");

        Assert.Empty(warnings);
    }

    [Fact]
    public async Task Start_WithNoSecureSocketAndNoCredentials_LogsNoWarning()
    {
        var warnings = await StartAndCollectWarningsAsync("None", null, null);

        Assert.Empty(warnings);
    }

    private sealed class ListLoggerProvider : ILoggerProvider
    {
        public List<string> Warnings { get; } = [];

        public ILogger CreateLogger(string categoryName) => new ListLogger(Warnings);

        public void Dispose()
        {
        }

        private sealed class ListLogger(List<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                {
                    warnings.Add(formatter(state, exception));
                }
            }
        }
    }
}
