namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// Loads the JWT keys at startup, so an unusable key stops the host before it serves requests, and logs a warning when the
/// signing key is ephemeral. <see cref="JwtKeyProvider"/> is resolved in <see cref="StartAsync"/>, after the options validation,
/// and not in the constructor: an invalid configuration then fails with its <c>OptionsValidationException</c> while the host
/// can still be stopped and disposed.
/// </summary>
public sealed partial class JwtStartupCheck(IServiceProvider serviceProvider, ILogger<JwtStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var keyProvider = serviceProvider.GetRequiredService<JwtKeyProvider>();

        if (keyProvider.IsEphemeral)
        {
            LogEphemeralKey(logger, keyProvider.CurrentKeyId);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "No JWT signing key is configured; using ephemeral key {KeyId}. Tokens die on restart")]
    private static partial void LogEphemeralKey(ILogger logger, string keyId);
}
