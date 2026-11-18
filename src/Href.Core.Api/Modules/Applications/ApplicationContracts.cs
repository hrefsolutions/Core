using System.ComponentModel.DataAnnotations;

namespace Href.Core.Api.Modules.Applications;

public sealed record CreateApplicationRequest(
    [Required, StringLength(Application.AppKeyMaxLength, MinimumLength = Application.AppKeyMinLength)]
    string AppKey,
    [Required, StringLength(Application.NameMaxLength, MinimumLength = 1)]
    string Name,
    [StringLength(Application.DescriptionMaxLength)]
    string? Description,
    bool AllowSelfRegistration = false);

public sealed record UpdateApplicationRequest(
    [Required, StringLength(Application.NameMaxLength, MinimumLength = 1)]
    string Name,
    [StringLength(Application.DescriptionMaxLength)]
    string? Description,
    bool AllowSelfRegistration = false);

public sealed record UpdateApplicationStatusRequest(
    [Required]
    string Status);

public sealed record ApplicationResponse(
    Guid Id,
    string AppKey,
    string Name,
    string? Description,
    string Status,
    bool AllowSelfRegistration,
    DateTime ClientSecretRotatedAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    public static ApplicationResponse From(Application application) =>
        new(
            application.Id,
            application.AppKey,
            application.Name,
            application.Description,
            application.Status.ToString(),
            application.AllowSelfRegistration,
            application.ClientSecretRotatedAtUtc,
            application.CreatedAtUtc,
            application.UpdatedAtUtc);
}

public sealed record ApplicationCreatedResponse(
    Guid Id,
    string AppKey,
    string Name,
    string? Description,
    string Status,
    bool AllowSelfRegistration,
    string ClientSecret,
    DateTime ClientSecretRotatedAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    public static ApplicationCreatedResponse From(Application application, string clientSecret) =>
        new(
            application.Id,
            application.AppKey,
            application.Name,
            application.Description,
            application.Status.ToString(),
            application.AllowSelfRegistration,
            clientSecret,
            application.ClientSecretRotatedAtUtc,
            application.CreatedAtUtc,
            application.UpdatedAtUtc);
}

public sealed record RotateClientSecretResponse(string ClientSecret);

public sealed record CurrentApplicationResponse(
    string AppKey,
    string Name,
    string Status)
{
    public static CurrentApplicationResponse From(Application application) =>
        new(
            application.AppKey,
            application.Name,
            application.Status.ToString());
}
