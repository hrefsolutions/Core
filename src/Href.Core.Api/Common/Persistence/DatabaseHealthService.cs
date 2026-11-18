using ISession = NHibernate.ISession;

namespace Href.Core.Api.Common.Persistence;

/// <summary>
/// Checks PostgreSQL connectivity by running <c>SELECT 1</c> on the request <see cref="ISession"/>.
/// </summary>
public sealed class DatabaseHealthService(ISession session, ILogger<DatabaseHealthService> logger)
{
    /// <summary>
    /// Returns <see langword="true"/> when PostgreSQL answers; cancellation is propagated, not reported as unhealthy.
    /// </summary>
    public async Task<bool> IsDatabaseHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await session.CreateSQLQuery("SELECT 1").UniqueResultAsync<object>(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Database health check failed");
            return false;
        }
    }
}
