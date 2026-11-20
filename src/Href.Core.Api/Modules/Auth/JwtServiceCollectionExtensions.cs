using Microsoft.Extensions.Options;

namespace Href.Core.Api.Modules.Auth;

public static class JwtServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="JwtOptions"/> (issuer without trailing slash, validated on startup), the singleton
    /// <see cref="JwtKeyProvider"/> and <see cref="JwtStartupCheck"/>.
    /// </summary>
    public static IServiceCollection AddJwtSigning(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .PostConfigure(options => options.Issuer = (options.Issuer ?? string.Empty).Trim().TrimEnd('/'))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();
        services.AddSingleton<JwtKeyProvider>();
        services.AddHostedService<JwtStartupCheck>();
        return services;
    }
}
