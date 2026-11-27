using System.Security.Claims;
using System.Text.Json;
using Href.Core.Api.Common.Web;
using Href.Core.Api.Modules.Applications;
using NHibernate.Linq;
using ISession = NHibernate.ISession;

namespace Href.Core.Api.Modules.Audit;

/// <summary>
/// Persists append-only <see cref="AuditLog"/> records using the request's <see cref="ISession"/>
/// so audit entries commit or roll back atomically with the business change.
/// </summary>
public sealed class AuditService(
    ISession session,
    IHttpContextAccessor httpContextAccessor,
    TimeProvider timeProvider) : IAuditService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, Guid?> _applicationIdsByKey = new(StringComparer.Ordinal);

    public async Task<AuditLog> RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var httpContext = httpContextAccessor.HttpContext;
        var (actorType, actorId) = ResolveActor(entry, httpContext);
        var appKey = entry.AppKeyOverride ?? httpContext?.User.GetAppKey();
        var applicationId = await ResolveApplicationIdAsync(entry, appKey, httpContext, cancellationToken);
        var correlationId = ResolveCorrelationId(httpContext);
        var ipAddress = httpContext is null ? null : EndUserIp.Resolve(httpContext);
        var userAgent = httpContext?.Request.Headers.UserAgent.ToString();

        var auditLog = AuditLog.Create(
            occurredAtUtc: timeProvider.GetUtcNow().UtcDateTime,
            action: entry.Action,
            actorType: actorType,
            actorId: actorId,
            appKey: appKey,
            entityType: entry.EntityType,
            entityId: entry.EntityId,
            oldValues: SerializeSnapshot(entry.OldValues),
            newValues: SerializeSnapshot(entry.NewValues),
            correlationId: correlationId,
            ipAddress: ipAddress,
            userAgent: userAgent,
            applicationId: applicationId);

        await session.SaveAsync(auditLog, cancellationToken);
        return auditLog;
    }

    /// <summary>
    /// The explicit <see cref="AuditEntry.ApplicationIdOverride"/>; otherwise the caller's <c>app_id</c> claim when the key
    /// is the caller's own; otherwise the id of the application that holds <paramref name="appKey"/> now (looked up once
    /// per scope). Writes that delete an application pass the override, because the lookup would no longer find it.
    /// </summary>
    private async Task<Guid?> ResolveApplicationIdAsync(
        AuditEntry entry,
        string? appKey,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        if (entry.ApplicationIdOverride is { } overrideId)
        {
            return overrideId;
        }

        if (string.IsNullOrWhiteSpace(appKey))
        {
            return null;
        }

        if (entry.AppKeyOverride is null && httpContext?.User.GetAppId() is { } claimId)
        {
            return claimId;
        }

        if (_applicationIdsByKey.TryGetValue(appKey, out var cached))
        {
            return cached;
        }

        var ids = await session.Query<Application>()
            .Where(application => application.AppKey == appKey)
            .Select(application => application.Id)
            .ToListAsync(cancellationToken);

        Guid? resolved = ids.Count == 1 ? ids[0] : null;
        _applicationIdsByKey[appKey] = resolved;
        return resolved;
    }

    private static (AuditActorType ActorType, string? ActorId) ResolveActor(AuditEntry entry, HttpContext? httpContext)
    {
        if (entry.ActorTypeOverride.HasValue)
        {
            return (entry.ActorTypeOverride.Value, entry.ActorIdOverride);
        }

        if (httpContext is null)
        {
            return (AuditActorType.System, entry.ActorIdOverride);
        }

        var userId = httpContext.User.FindFirstValue("sub")
            ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!string.IsNullOrWhiteSpace(userId))
        {
            return (AuditActorType.User, userId);
        }

        var appKey = httpContext.User.GetAppKey();

        if (!string.IsNullOrWhiteSpace(appKey))
        {
            return (AuditActorType.Application, appKey);
        }

        return (AuditActorType.Anonymous, null);
    }

    private static string? ResolveCorrelationId(HttpContext? httpContext)
    {
        if (httpContext is null)
        {
            return null;
        }

        if (httpContext.Items.TryGetValue(CorrelationIdMiddleware.HeaderName, out var item)
            && item is string correlationId
            && !string.IsNullOrWhiteSpace(correlationId))
        {
            return correlationId;
        }

        var headerValue = httpContext.Request.Headers[CorrelationIdMiddleware.HeaderName].ToString();
        return string.IsNullOrWhiteSpace(headerValue) ? null : headerValue;
    }

    /// <summary>
    /// Serializes an audit snapshot the way it is stored in <c>old_values</c> / <c>new_values</c> (web defaults: camelCase).
    /// </summary>
    internal static string? SerializeSnapshot(object? snapshot) =>
        snapshot switch
        {
            null => null,
            string rawJson => string.IsNullOrWhiteSpace(rawJson) ? null : rawJson,
            _ => JsonSerializer.Serialize(snapshot, JsonOptions),
        };
}
