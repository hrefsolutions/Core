using Href.Core.Api.Modules.Notifications.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SmtpConfigurationStartupTests(PostgresFixture postgres)
{
    [Fact]
    public void Api_WithMissingSmtpHost_DoesNotStart()
    {
        using var factory = new ApiFactory(
            postgres.ConnectionString,
            overrides: new Dictionary<string, string?> { ["Smtp:Host"] = string.Empty });

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
    }

    [Fact]
    public void Api_WithValidSmtpConfiguration_Starts()
    {
        using var factory = new ApiFactory(postgres.ConnectionString);

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }

    [Fact]
    public async Task HostStart_WithMissingSmtpHost_ThrowsOptionsValidationException()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Smtp:Host"] = string.Empty,
            ["Smtp:Port"] = "1025",
            ["Smtp:SecureSocket"] = "None",
            ["Smtp:FromEmail"] = "href.notifier@gmail.com",
            ["Smtp:FromName"] = "Href",
        });
        builder.Services.AddSmtpEmail(builder.Configuration);
        using var host = builder.Build();

        var exception = await Record.ExceptionAsync(() => host.StartAsync(TestContext.Current.CancellationToken));

        var validationException = Assert.IsType<OptionsValidationException>(exception);
        Assert.Equal(typeof(SmtpOptions), validationException.OptionsType);
    }
}
