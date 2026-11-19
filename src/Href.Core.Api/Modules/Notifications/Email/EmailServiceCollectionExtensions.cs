using Microsoft.Extensions.Options;

namespace Href.Core.Api.Modules.Notifications.Email;

public static class EmailServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IEmailSender"/> and <see cref="SmtpOptions"/>, validated on startup.
    /// </summary>
    public static IServiceCollection AddSmtpEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<SmtpOptions>()
            .Bind(configuration.GetSection(SmtpOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SmtpOptions>>(new SmtpOptionsValidator(configuration));
        services.AddHostedService<SmtpStartupCheck>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        return services;
    }
}
