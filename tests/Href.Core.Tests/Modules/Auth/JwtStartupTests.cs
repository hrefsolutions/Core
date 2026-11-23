using Href.Core.Api.Modules.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Href.Core.Tests.Modules.Auth;

public sealed class JwtStartupTests
{
    [Fact]
    public async Task HostStart_InProductionWithoutSigningKey_ThrowsOptionsValidationException()
    {
        using var host = BuildHost(Environments.Production, new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "https://core.example.com",
        });

        var exception = await Record.ExceptionAsync(() => host.StartAsync(TestContext.Current.CancellationToken));

        var validationException = Assert.IsType<OptionsValidationException>(exception);
        Assert.Equal(typeof(JwtOptions), validationException.OptionsType);
    }

    [Fact]
    public async Task HostStart_InProductionWithSigningKey_StartsAndUsesIt()
    {
        using var host = BuildHost(Environments.Production, new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "https://core.example.com/",
            ["Jwt:SigningKey:KeyId"] = "core-2026-11",
            ["Jwt:SigningKey:PrivateKeyPem"] = JwtTestKeys.CurrentPrivateKeyPem,
            ["Jwt:PreviousPublicKeys:0:KeyId"] = "core-2026-05",
            ["Jwt:PreviousPublicKeys:0:PublicKeyPem"] = JwtTestKeys.PreviousPublicKeyPem,
        });

        await host.StartAsync(TestContext.Current.CancellationToken);

        var provider = host.Services.GetRequiredService<JwtKeyProvider>();
        Assert.Equal(["core-2026-11", "core-2026-05"], provider.JsonWebKeySet.Keys.Select(key => key.Kid));
        Assert.Equal("https://core.example.com", host.Services.GetRequiredService<IOptions<JwtOptions>>().Value.Issuer);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HostStart_InProductionWithInvalidPem_ThrowsOptionsValidationException()
    {
        using var host = BuildHost(Environments.Production, new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "https://core.example.com",
            ["Jwt:SigningKey:KeyId"] = "core-2026-11",
            ["Jwt:SigningKey:PrivateKeyPem"] = "not a pem",
        });

        var exception = await Record.ExceptionAsync(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.IsType<OptionsValidationException>(exception);
    }

    [Fact]
    public async Task StartupCheck_WithEphemeralKey_LogsWarning()
    {
        var options = JwtOptionsValidatorTests.ValidOptions();
        options.SigningKey = new JwtSigningKeyOptions();
        using var provider = JwtKeyProviderTests.CreateProvider(options, Environments.Development);

        var sink = await RunCheckAsync(provider);

        Assert.Contains(sink.Events, logEvent => logEvent.Level == LogEventLevel.Warning);
    }

    [Fact]
    public async Task StartupCheck_WithConfiguredKey_DoesNotLog()
    {
        using var provider = JwtKeyProviderTests.CreateProvider(JwtOptionsValidatorTests.ValidOptions(), Environments.Development);

        var sink = await RunCheckAsync(provider);

        Assert.Empty(sink.Events);
    }

    private static IHost BuildHost(string environmentName, Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environmentName });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddJwtSigning(builder.Configuration);
        return builder.Build();
    }

    private static async Task<CapturingLogSink> RunCheckAsync(JwtKeyProvider provider)
    {
        var sink = new CapturingLogSink();
        using var serilog = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        using var services = new ServiceCollection().AddSingleton(provider).BuildServiceProvider();
        var check = new JwtStartupCheck(services, new SerilogLoggerFactory(serilog).CreateLogger<JwtStartupCheck>());

        await check.StartAsync(TestContext.Current.CancellationToken);
        await check.StopAsync(TestContext.Current.CancellationToken);

        return sink;
    }
}
