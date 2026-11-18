using Href.Core.Api.Modules.Applications;
using NHibernate.Exceptions;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ApplicationPersistenceIntegrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task InsertAndLoad_RoundTripsAllPropertiesAndSetsAuditTimestamps()
    {
        var appKey = $"app-{Guid.NewGuid():N}"[..20];
        var (application, plainSecret) = Application.Create(
            appKey,
            "Portal App",
            "Main client portal",
            true,
            TimeProvider.System).Value;

        using var session = postgres.SessionFactory.OpenSession();

        using (var transaction = session.BeginTransaction())
        {
            await session.SaveAsync(application);
            await transaction.CommitAsync();
        }

        session.Clear();
        var loaded = await session.GetAsync<Application>(application.Id);

        Assert.NotNull(loaded);
        Assert.Equal(appKey, loaded.AppKey);
        Assert.Equal("Portal App", loaded.Name);
        Assert.Equal("Main client portal", loaded.Description);
        Assert.Equal(ApplicationStatus.Active, loaded.Status);
        Assert.True(loaded.AllowSelfRegistration);
        Assert.True(loaded.VerifySecret(plainSecret));
        Assert.NotEqual(default, loaded.CreatedAtUtc);
        Assert.Equal(loaded.CreatedAtUtc, loaded.UpdatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, loaded.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, loaded.UpdatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, loaded.ClientSecretRotatedAtUtc.Kind);
    }

    [Fact]
    public async Task Insert_WithDuplicateAppKey_ThrowsGenericADOException()
    {
        var appKey = $"dup-{Guid.NewGuid():N}"[..20];
        var (first, _) = Application.Create(appKey, "First App", null, false, TimeProvider.System).Value;
        var (second, _) = Application.Create(appKey, "Second App", null, false, TimeProvider.System).Value;

        using (var firstSession = postgres.SessionFactory.OpenSession())
        using (var firstTransaction = firstSession.BeginTransaction())
        {
            await firstSession.SaveAsync(first);
            await firstTransaction.CommitAsync();
        }

        using var secondSession = postgres.SessionFactory.OpenSession();
        using var secondTransaction = secondSession.BeginTransaction();
        await secondSession.SaveAsync(second);

        await Assert.ThrowsAsync<GenericADOException>(() => secondTransaction.CommitAsync());
    }
}
