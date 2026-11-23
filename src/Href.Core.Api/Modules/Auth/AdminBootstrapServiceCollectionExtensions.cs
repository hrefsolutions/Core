using Microsoft.Extensions.Options;

namespace Href.Core.Api.Modules.Auth;

public static class AdminBootstrapServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AdminBootstrapOptions"/> (validated on startup) and the <see cref="AdminBootstrapper"/> hosted service.
    /// </summary>
    public static IServiceCollection AddAdminBootstrap(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<AdminBootstrapOptions>()
            .Bind(configuration.GetSection(AdminBootstrapOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AdminBootstrapOptions>, AdminBootstrapOptionsValidator>();
        services.AddHostedService<AdminBootstrapper>();
        return services;
    }
}
