using Href.Core.Api.Common.Results;
using NHibernate.Linq;
using ISession = NHibernate.ISession;

namespace Href.Core.Api.Modules.Applications;

/// <summary>
/// Orchestrates application persistence and transactions over <see cref="ISession"/>.
/// </summary>
public sealed class ApplicationService(ISession session, TimeProvider timeProvider)
{
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

        await session.SaveAsync(application, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

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

        var application = await FindByAppKeyAsync(appKey, cancellationToken);

        if (application is null)
        {
            return ApplicationErrors.NotFound;
        }

        var updateResult = application.Update(request.Name, request.Description, request.AllowSelfRegistration);

        if (updateResult.IsFailure)
        {
            return updateResult.Error;
        }

        await session.FlushAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ApplicationResponse.From(application);
    }

    public async Task<Result<ApplicationResponse>> UpdateStatusAsync(
        string appKey,
        UpdateApplicationStatusRequest request,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ApplicationStatus>(request.Status, ignoreCase: false, out var targetStatus)
            || !Enum.IsDefined(targetStatus))
        {
            return ApplicationErrors.InvalidStatus;
        }

        using var transaction = session.BeginTransaction();

        var application = await FindByAppKeyAsync(appKey, cancellationToken);

        if (application is null)
        {
            return ApplicationErrors.NotFound;
        }

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

        await session.FlushAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ApplicationResponse.From(application);
    }

    public async Task<Result<RotateClientSecretResponse>> RotateSecretAsync(
        string appKey,
        CancellationToken cancellationToken)
    {
        using var transaction = session.BeginTransaction();

        var application = await FindByAppKeyAsync(appKey, cancellationToken);

        if (application is null)
        {
            return ApplicationErrors.NotFound;
        }

        var clientSecret = application.RotateSecret(timeProvider);

        await session.FlushAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new RotateClientSecretResponse(clientSecret);
    }

    private async Task<Application?> FindByAppKeyAsync(string appKey, CancellationToken cancellationToken) =>
        await session.Query<Application>()
            .FirstOrDefaultAsync(application => application.AppKey == appKey, cancellationToken);
}
