using System.Net;
using System.Security.Claims;
using Href.Core.Api.Common.Web;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using NHibernate.Exceptions;
using Npgsql;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AuditLogIntegrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task RecordAsync_WithHttpContext_PersistsAuditLogWithJsonbSnapshotsAndRequestContext()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 20, 0, 0, TimeSpan.Zero));
        var httpContext = new DefaultHttpContext();
        httpContext.Items[CorrelationIdMiddleware.HeaderName] = "corr-iso9001-01";
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.44");
        httpContext.Request.Headers.UserAgent = "HrefSatellite/1.0";
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "user-42"),
            new Claim(AppKeyAuthentication.AppKeyClaimType, "billing-app"),
        ], "TestAuth"));

        using var session = postgres.SessionFactory.OpenSession();
        var auditService = new AuditService(session, new StubHttpContextAccessor(httpContext), timeProvider);

        AuditLog saved;

        using (var transaction = session.BeginTransaction())
        {
            saved = await auditService.RecordAsync(new AuditEntry(
                Action: AuditActions.UserUpdated,
                EntityType: "User",
                EntityId: "user-42",
                OldValues: new { FullName = "Old Name", IsLocked = false },
                NewValues: new { FullName = "New Name", IsLocked = true }),
                TestContext.Current.CancellationToken);

            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        session.Clear();
        var reloaded = await session.GetAsync<AuditLog>(saved.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(reloaded);
        Assert.Equal(timeProvider.GetUtcNow().UtcDateTime, reloaded.OccurredAtUtc);
        Assert.Equal(DateTimeKind.Utc, reloaded.OccurredAtUtc.Kind);
        Assert.Equal(AuditActions.UserUpdated, reloaded.Action);
        Assert.Equal("User", reloaded.EntityType);
        Assert.Equal("user-42", reloaded.EntityId);
        Assert.Equal(AuditActorType.User, reloaded.ActorType);
        Assert.Equal("user-42", reloaded.ActorId);
        Assert.Equal("billing-app", reloaded.AppKey);
        Assert.Equal("corr-iso9001-01", reloaded.CorrelationId);
        Assert.Equal("192.0.2.44", reloaded.IpAddress);
        Assert.Equal("HrefSatellite/1.0", reloaded.UserAgent);
        Assert.Contains("\"fullName\"", reloaded.OldValues, StringComparison.Ordinal);
        Assert.Contains("\"Old Name\"", reloaded.OldValues, StringComparison.Ordinal);
        Assert.Contains("\"fullName\"", reloaded.NewValues, StringComparison.Ordinal);
        Assert.Contains("\"New Name\"", reloaded.NewValues, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordAsync_ResolvesApplicationAnonymousAndSystemActors()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 21, 0, 0, TimeSpan.Zero));
        using var session = postgres.SessionFactory.OpenSession();

        var appContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(AppKeyAuthentication.AppKeyClaimType, "crm-portal"),
            ], AppKeyAuthentication.SchemeName)),
        };
        var anonymousContext = new DefaultHttpContext();

        var appAudit = new AuditService(session, new StubHttpContextAccessor(appContext), timeProvider);
        var anonAudit = new AuditService(session, new StubHttpContextAccessor(anonymousContext), timeProvider);
        var systemAudit = new AuditService(session, new StubHttpContextAccessor(null), timeProvider);

        AuditLog appLog;
        AuditLog anonLog;
        AuditLog systemLog;

        using (var transaction = session.BeginTransaction())
        {
            appLog = await appAudit.RecordAsync(
                new AuditEntry(AuditActions.NotificationQueued),
                TestContext.Current.CancellationToken);
            anonLog = await anonAudit.RecordAsync(
                new AuditEntry(AuditActions.LoginFailed),
                TestContext.Current.CancellationToken);
            systemLog = await systemAudit.RecordAsync(
                new AuditEntry(AuditActions.TemplateCreated, ActorIdOverride: "template-seeder"),
                TestContext.Current.CancellationToken);

            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(AuditActorType.Application, appLog.ActorType);
        Assert.Equal("crm-portal", appLog.ActorId);
        Assert.Equal("crm-portal", appLog.AppKey);
        Assert.Equal(AuditActorType.Anonymous, anonLog.ActorType);
        Assert.Null(anonLog.ActorId);
        Assert.Equal(AuditActorType.System, systemLog.ActorType);
        Assert.Equal("template-seeder", systemLog.ActorId);
    }

    [Fact]
    public async Task AuditLogsTable_RejectsUpdateAndDeleteViaDatabaseTrigger()
    {
        using var session = postgres.SessionFactory.OpenSession();
        var auditService = new AuditService(session, new StubHttpContextAccessor(null), TimeProvider.System);
        AuditLog saved;

        using (var transaction = session.BeginTransaction())
        {
            saved = await auditService.RecordAsync(
                new AuditEntry(AuditActions.ApplicationCreated, EntityType: "Application", EntityId: "app-1"),
                TestContext.Current.CancellationToken);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        using var mutationSession = postgres.SessionFactory.OpenSession();

        await Assert.ThrowsAsync<GenericADOException>(() =>
            mutationSession.CreateSQLQuery("UPDATE audit_logs SET action = 'TAMPERED' WHERE id = :id")
                .SetParameter("id", saved.Id)
                .ExecuteUpdateAsync(TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<GenericADOException>(() =>
            mutationSession.CreateSQLQuery("DELETE FROM audit_logs WHERE id = :id")
                .SetParameter("id", saved.Id)
                .ExecuteUpdateAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AuditLogsTable_RejectsTruncateViaDatabaseTrigger()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using (var session = postgres.SessionFactory.OpenSession())
        {
            var auditService = new AuditService(session, new StubHttpContextAccessor(null), TimeProvider.System);
            using var transaction = session.BeginTransaction();
            await auditService.RecordAsync(
                new AuditEntry(AuditActions.ApplicationCreated, EntityType: "Application", EntityId: "truncate-check"),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transactionScope = await connection.BeginTransactionAsync(cancellationToken);
        PostgresException? rejection = null;

        try
        {
            await using var truncate = new NpgsqlCommand("TRUNCATE audit_logs", connection, transactionScope);
            await truncate.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception)
        {
            rejection = exception;
        }
        finally
        {
            await transactionScope.RollbackAsync(cancellationToken);
        }

        Assert.NotNull(rejection);
        Assert.Contains("append-only", rejection.MessageText, StringComparison.Ordinal);

        await using var count = new NpgsqlCommand("SELECT count(*) FROM audit_logs WHERE entity_id = 'truncate-check'", connection);
        Assert.True((long)(await count.ExecuteScalarAsync(cancellationToken))! >= 1);
    }

    [Fact]
    public async Task RecordAsync_WhenBusinessTransactionRollsBack_DropsAuditRowAtomically()
    {
        var appKey = $"rb-{Guid.NewGuid():N}"[..20];
        var (application, _) = Application.Create(appKey, "Rollback App", null, false, TimeProvider.System).Value;
        Guid auditLogId;

        using (var session = postgres.SessionFactory.OpenSession())
        {
            var auditService = new AuditService(session, new StubHttpContextAccessor(null), TimeProvider.System);
            using var transaction = session.BeginTransaction();

            await session.SaveAsync(application, TestContext.Current.CancellationToken);
            var auditLog = await auditService.RecordAsync(
                new AuditEntry(
                    AuditActions.ApplicationCreated,
                    EntityType: nameof(Application),
                    EntityId: application.Id.ToString(),
                    NewValues: new { application.AppKey, application.Name }),
                TestContext.Current.CancellationToken);
            auditLogId = auditLog.Id;

            await session.FlushAsync(TestContext.Current.CancellationToken);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        using var verifySession = postgres.SessionFactory.OpenSession();
        var persistedApp = await verifySession.GetAsync<Application>(application.Id, TestContext.Current.CancellationToken);
        var persistedAudit = await verifySession.GetAsync<AuditLog>(auditLogId, TestContext.Current.CancellationToken);

        Assert.Null(persistedApp);
        Assert.Null(persistedAudit);
    }

    private sealed class StubHttpContextAccessor(HttpContext? httpContext) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }
}
