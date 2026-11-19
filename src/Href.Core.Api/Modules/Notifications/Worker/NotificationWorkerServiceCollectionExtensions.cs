using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Href.Core.Api.Modules.Notifications.Worker;

public static class NotificationWorkerServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="NotificationWorkerOptions"/> (validated on startup), the scoped
    /// <see cref="INotificationDispatcher"/>, the <see cref="NotificationWorkerStartupCheck"/> and the
    /// <see cref="NotificationBackgroundWorker"/> hosted services.
    /// Requires <c>AddNHibernate</c>, <see cref="Email.IEmailSender"/> and <see cref="ITemplateRenderer"/>.
    /// </summary>
    public static IServiceCollection AddNotificationWorker(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<NotificationWorkerOptions>()
            .Bind(configuration.GetSection(NotificationWorkerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<INotificationDispatcher, NotificationDispatcher>();
        services.AddHostedService<NotificationWorkerStartupCheck>();
        services.AddHostedService<NotificationBackgroundWorker>();
        return services;
    }
}
