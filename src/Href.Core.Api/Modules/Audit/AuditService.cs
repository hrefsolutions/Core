using System.Security.Claims;
using System.Text.Json;
using Href.Core.Api.Common.Web;
using Href.Core.Api.Modules.Applications;
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

    public async Task<AuditLog> RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var httpContext = httpContextAccessor.HttpContext;
        var (actorType, actorId) = ResolveActor(entry, httpContext);
        var appKey = entry.AppKeyOverride ?? httpContext?.User.GetAppKey();
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
            userAgent: userAgent);

        await session.SaveAsync(auditLog, cancellationToken);
        return auditLog;
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
