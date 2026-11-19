using System.Text.Json;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Users;
using Href.Core.Tests.Modules.Audit;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Href.Core.Tests.Modules.Users;

public sealed class UserAuditingTests
{
    private const string Password = "correct horse battery";

    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    private static readonly PasswordHasher<User> Hasher = new(
        Options.Create(new PasswordHasherOptions { IterationCount = 1_000 }));

    [Fact]
    public void Snapshot_SerializesOnlySafeFieldsInCamelCase()
    {
        var user = User.Create("ana@example.com", "Ana Pérez", Password, Hasher).Value;
        user.MarkEmailVerified(Now);
        user.RegisterLogin(Now);
        user.Lock("Suspicious activity", Now);

        var json = AuditService.SerializeSnapshot(UserAuditSnapshot.From(user));

        Assert.NotNull(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(
            ["email", "emailVerifiedAtUtc", "fullName", "id", "lockReason", "lockedAtUtc", "status"],
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(user.Id, root.GetProperty("id").GetGuid());
        Assert.Equal("ana@example.com", root.GetProperty("email").GetString());
        Assert.Equal("Ana Pérez", root.GetProperty("fullName").GetString());
        Assert.Equal("Locked", root.GetProperty("status").GetString());
        Assert.Equal(Now, root.GetProperty("emailVerifiedAtUtc").GetDateTime());
        Assert.Equal(Now, root.GetProperty("lockedAtUtc").GetDateTime());
        Assert.Equal("Suspicious activity", root.GetProperty("lockReason").GetString());
        Assert.DoesNotContain(user.PasswordHash, json, StringComparison.Ordinal);
        Assert.DoesNotContain(user.SecurityStamp.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Password, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordUserChangeAsync_WithBeforeAndAfter_RecordsBothSnapshotsForTheUser()
    {
        var auditService = new CapturingAuditService();
        var user = User.Create("ana@example.com", "Ana", Password, Hasher).Value;
        var before = UserAuditSnapshot.From(user);
        user.Lock("reason", Now);
        var after = UserAuditSnapshot.From(user);

        await auditService.RecordUserChangeAsync(AuditActions.UserLocked, before, after, TestContext.Current.CancellationToken);

        var entry = Assert.Single(auditService.Entries);
        Assert.Equal("USER_LOCKED", entry.Action);
        Assert.Equal("User", entry.EntityType);
        Assert.Equal(user.Id.ToString(), entry.EntityId);
        Assert.Same(before, entry.OldValues);
        Assert.Same(after, entry.NewValues);
        Assert.Null(entry.AppKeyOverride);
        Assert.Null(entry.ActorTypeOverride);
    }

    [Fact]
    public async Task RecordUserChangeAsync_ForCreation_HasNoOldValues()
    {
        var auditService = new CapturingAuditService();
        var user = User.Create("ana@example.com", "Ana", Password, Hasher).Value;

        await auditService.RecordUserChangeAsync(AuditActions.UserCreated, null, UserAuditSnapshot.From(user), TestContext.Current.CancellationToken);

        var entry = Assert.Single(auditService.Entries);
        Assert.Equal("USER_CREATED", entry.Action);
        Assert.Null(entry.OldValues);
        Assert.Equal(user.Id.ToString(), entry.EntityId);
    }

    [Fact]
    public async Task RecordUserChangeAsync_WithoutSnapshotsOrWithDifferentUsers_Throws()
    {
        var auditService = new CapturingAuditService();
        var first = UserAuditSnapshot.From(User.Create("ana@example.com", "Ana", Password, Hasher).Value);
        var second = UserAuditSnapshot.From(User.Create("bob@example.com", "Bob", Password, Hasher).Value);
        var cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(() => auditService.RecordUserChangeAsync(AuditActions.UserUpdated, null, null, cancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => auditService.RecordUserChangeAsync(AuditActions.UserUpdated, first, second, cancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => auditService.RecordUserChangeAsync(" ", first, first, cancellationToken));
        Assert.Empty(auditService.Entries);
    }
}
