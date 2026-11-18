using System.Text.RegularExpressions;
using Href.Core.Api.Common.Domain;
using Href.Core.Api.Common.Results;

namespace Href.Core.Api.Modules.Applications;

/// <summary>
/// Represents a client (satellite) application registered in Href.Core.
/// </summary>
public partial class Application : Entity, IAuditableEntity
{
    public const int AppKeyMinLength = 3;
    public const int AppKeyMaxLength = 50;
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;
    public const int SecretHashLength = 64;

    protected Application()
    {
    }

    public virtual string AppKey { get; protected set; } = string.Empty;

    public virtual string Name { get; protected set; } = string.Empty;

    public virtual string? Description { get; protected set; }

    public virtual ApplicationStatus Status { get; protected set; } = ApplicationStatus.Active;

    public virtual string ClientSecretHash { get; protected set; } = string.Empty;

    public virtual DateTime ClientSecretRotatedAtUtc { get; protected set; }

    public virtual bool AllowSelfRegistration { get; protected set; }

    public virtual DateTime CreatedAtUtc { get; protected set; }

    public virtual DateTime UpdatedAtUtc { get; protected set; }

    public static Result<(Application Application, string ClientSecret)> Create(
        string? appKey,
        string? name,
        string? description,
        bool allowSelfRegistration,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        var normalizedKey = appKey?.Trim() ?? string.Empty;

        if (!IsValidAppKey(normalizedKey))
        {
            return ApplicationErrors.InvalidAppKey;
        }

        var validationResult = ValidateDetails(name, description, out var normalizedName, out var normalizedDescription);

        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        var plainSecret = ClientSecretGenerator.Generate();
        var application = new Application
        {
            AppKey = normalizedKey,
            Name = normalizedName,
            Description = normalizedDescription,
            Status = ApplicationStatus.Active,
            ClientSecretHash = ClientSecretGenerator.Hash(plainSecret),
            ClientSecretRotatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            AllowSelfRegistration = allowSelfRegistration,
        };

        return (application, plainSecret);
    }

    public virtual Result Update(string? name, string? description, bool allowSelfRegistration)
    {
        var validationResult = ValidateDetails(name, description, out var normalizedName, out var normalizedDescription);

        if (validationResult.IsFailure)
        {
            return validationResult;
        }

        Name = normalizedName;
        Description = normalizedDescription;
        AllowSelfRegistration = allowSelfRegistration;

        return Result.Success();
    }

    public virtual Result Suspend()
    {
        if (Status == ApplicationStatus.Suspended)
        {
            return ApplicationErrors.AlreadySuspended;
        }

        Status = ApplicationStatus.Suspended;
        return Result.Success();
    }

    public virtual Result Activate()
    {
        if (Status == ApplicationStatus.Active)
        {
            return ApplicationErrors.AlreadyActive;
        }

        Status = ApplicationStatus.Active;
        return Result.Success();
    }

    public virtual string RotateSecret(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        var plainSecret = ClientSecretGenerator.Generate();
        ClientSecretHash = ClientSecretGenerator.Hash(plainSecret);
        ClientSecretRotatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        return plainSecret;
    }

    public virtual bool VerifySecret(string? plainSecret) =>
        ClientSecretGenerator.Verify(plainSecret, ClientSecretHash);

    private static bool IsValidAppKey(string value) =>
        value.Length is >= AppKeyMinLength and <= AppKeyMaxLength
        && AppKeyRegex().IsMatch(value);

    private static Result ValidateDetails(
        string? name,
        string? description,
        out string normalizedName,
        out string? normalizedDescription)
    {
        normalizedName = name?.Trim() ?? string.Empty;
        normalizedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

        if (normalizedName.Length is 0 or > NameMaxLength)
        {
            return ApplicationErrors.InvalidName;
        }

        if (normalizedDescription is not null && normalizedDescription.Length > DescriptionMaxLength)
        {
            return ApplicationErrors.InvalidDescription;
        }

        return Result.Success();
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex AppKeyRegex();
}
