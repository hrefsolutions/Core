using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Auth;
using NHibernate;
using NHibernate.Linq;
using Npgsql;
using ISession = NHibernate.ISession;

namespace Href.Core.Api.Modules.Applications;

/// <summary>
/// Orchestrates application persistence and transactions over <see cref="ISession"/>. Every write locks the application
/// row (<c>SELECT ... FOR UPDATE</c>) and records its <c>APPLICATION_*</c> audit entry in the same transaction.
/// </summary>
public sealed class ApplicationService(ISession session, TimeProvider timeProvider, IAuditService auditService)
{
    private const string AppKeyUniqueIndexName = "ux_applications_app_key";

    private static readonly string DummySecretHash = new('0', Application.SecretHashLength);

    public async Task<Result<Application>> AuthenticateAsync(
        string appKey,
        string plainSecret,
        CancellationToken cancellationToken)
    {
        var application = await FindByAppKeyAsync(appKey, cancellationToken);

        if (application is null)
        {
            ClientSecretGenerator.Verify(plainSecret, DummySecretHash);
            return AppKeyAuthentication.InvalidCredentialsError;
        }

        if (!application.VerifySecret(plainSecret))
        {
            return AppKeyAuthentication.InvalidCredentialsError;
        }

        if (application.Status == ApplicationStatus.Suspended)
        {
            return AppKeyAuthentication.ApplicationSuspendedError;
        }

        return application;
    }

    public async Task<Result<CurrentApplicationResponse>> GetCurrentAsync(
        string appKey,
        CancellationToken cancellationToken)
    {
        var application = await FindByAppKeyAsync(appKey, cancellationToken);

        if (application is null)
        {
            return ApplicationErrors.NotFound;
        }

        return CurrentApplicationResponse.From(application);
    }

    public async Task<Result<ApplicationCreatedResponse>> CreateAsync(
        CreateApplicationRequest request,
        CancellationToken cancellationToken)
    {
        var createResult = Application.Create(
            request.AppKey,
            request.Name,
            request.Description,
            request.AllowSelfRegistration,
            timeProvider);

        if (createResult.IsFailure)
        {
            return createResult.Error;
        }

        var (application, clientSecret) = createResult.Value;

        using var transaction = session.BeginTransaction();

        var exists = await session.Query<Application>()
            .AnyAsync(existing => existing.AppKey == application.AppKey, cancellationToken);

        if (exists)
        {
            return ApplicationErrors.AppKeyTaken;
        }

        try
        {
            await session.SaveAsync(application, cancellationToken);
            await auditService.RecordApplicationChangeAsync(
                AuditActions.ApplicationCreated,
                before: null,
                after: ApplicationAuditSnapshot.From(application),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (IsAppKeyUniqueViolation(exception))
        {
            return ApplicationErrors.AppKeyTaken;
        }

        return ApplicationCreatedResponse.From(application, clientSecret);
    }

    public async Task<IReadOnlyList<ApplicationResponse>> ListAsync(CancellationToken cancellationToken)
    {
        var applications = await session.Query<Application>()
            .OrderBy(application => application.Name)
            .ToListAsync(cancellationToken);

        return applications.Select(ApplicationResponse.From).ToList();
    }

    public async Task<Result<ApplicationResponse>> GetByAppKeyAsync(
        string appKey,
        CancellationToken cancellationToken)
    {
        var application = await FindByAppKeyAsync(appKey, cancellationToken);

        if (application is null)
        {
            return ApplicationErrors.NotFound;
        }

        return ApplicationResponse.From(application);
    }

    public async Task<Result<ApplicationResponse>> UpdateAsync(
        string appKey,
        UpdateApplicationRequest request,
        CancellationToken cancellationToken)
    {
        using var transaction = session.BeginTransaction();

        var application = await FindByAppKeyAsync(appKey, cancellationToken, lockForUpdate: true);

        if (application is null)
        {
            return ApplicationErrors.NotFound;
        }

        if (request.AllowSelfRegistration && application.Id == CoreApplication.Id)
        {
            return ApplicationErrors.Reserved;
        }

        var before = ApplicationAuditSnapshot.From(application);
        var updateResult = application.Update(request.Name, request.Description, request.AllowSelfRegistration);

        if (updateResult.IsFailure)
        {
            return updateResult.Error;
        }

        var after = ApplicationAuditSnapshot.From(application);

        if (after == before)
        {
            return ApplicationResponse.From(application);
        }

        await auditService.RecordApplicationChangeAsync(AuditActions.ApplicationUpdated, before, after, cancellationToken);
        await session.FlushAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ApplicationResponse.From(application);
    }

    public async Task<Result<ApplicationResponse>> UpdateStatusAsync(
        string appKey,
        UpdateApplicationStatusRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryParseStatusName(request.Status, out var targetStatus))
        {
            return ApplicationErrors.InvalidStatus;
        }

        using var transaction = session.BeginTransaction();

        var application = await FindByAppKeyAsync(appKey, cancellationToken, lockForUpdate: true);

        if (application is null)
        {
            return ApplicationErrors.NotFound;
        }

        if (targetStatus == ApplicationStatus.Suspended && application.Id == CoreApplication.Id)
        {
            return ApplicationErrors.Reserved;
        }

        var before = ApplicationAuditSnapshot.From(application);
        var statusResult = targetStatus switch
        {
            ApplicationStatus.Active => application.Activate(),
            ApplicationStatus.Suspended => application.Suspend(),
            _ => ApplicationErrors.InvalidStatus,
        };

        if (statusResult.IsFailure)
        {
            return statusResult.Error;
        }

        await auditService.RecordApplicationChangeAsync(
            AuditActions.ApplicationStatusChanged,
            before,
            ApplicationAuditSnapshot.From(application),
            cancellationToken);
        await session.FlushAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ApplicationResponse.From(application);
    }

    public async Task<Result<RotateClientSecretResponse>> RotateSecretAsync(
        string appKey,
        CancellationToken cancellationToken)
    {
        using var transaction = session.BeginTransaction();

        var application = await FindByAppKeyAsync(appKey, cancellationToken, lockForUpdate: true);

        if (application is null)
        {
            return ApplicationErrors.NotFound;
        }

        var before = ApplicationAuditSnapshot.From(application);
        var clientSecret = application.RotateSecret(timeProvider);

        await auditService.RecordApplicationChangeAsync(
            AuditActions.ApplicationSecretRotated,
            before,
            ApplicationAuditSnapshot.From(application),
            cancellationToken);
        await session.FlushAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new RotateClientSecretResponse(clientSecret);
    }

    /// <summary>
    /// Accepts only the exact enum names (<c>Active</c>, <c>Suspended</c>); numeric or combined values are rejected.
    /// </summary>
    internal static bool TryParseStatusName(string? value, out ApplicationStatus status)
    {
        foreach (var candidate in Enum.GetValues<ApplicationStatus>())
        {
            if (string.Equals(candidate.ToString(), value, StringComparison.Ordinal))
            {
                status = candidate;
                return true;
            }
        }

        status = default;
        return false;
    }

    private static bool IsAppKeyUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: AppKeyUniqueIndexName })
            {
                return true;
            }
        }

        return false;
    }

    private async Task<Application?> FindByAppKeyAsync(
        string appKey,
        CancellationToken cancellationToken,
        bool lockForUpdate = false)
    {
        var query = session.Query<Application>();

        if (lockForUpdate)
        {
            query = query.WithLock(LockMode.Upgrade);
        }

        var rows = await query
            .Where(application => application.AppKey == appKey)
            .ToListAsync(cancellationToken);

        return rows.SingleOrDefault();
    }
}
