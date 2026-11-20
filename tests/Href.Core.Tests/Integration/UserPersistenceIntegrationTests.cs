using System.Buffers.Binary;
using Href.Core.Api.Modules.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NHibernate.Exceptions;
using Npgsql;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class UserPersistenceIntegrationTests(PostgresFixture postgres)
{
    private const string Password = "correct horse battery";

    private static readonly PasswordHasher<User> Hasher = new();

    [Fact]
    public async Task InsertAndLoad_RoundTripsAllPropertiesAndSetsAuditTimestamps()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var verifiedAt = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);
        var lockedAt = new DateTime(2026, 11, 19, 11, 0, 0, DateTimeKind.Utc);
        var loginAt = new DateTime(2026, 11, 19, 9, 30, 0, DateTimeKind.Utc);
        var email = UniqueEmail();
        var user = User.Create(email.ToUpperInvariant(), "Ana Pérez", Password, Hasher).Value;
        user.MarkEmailVerified(verifiedAt);
        user.RegisterLogin(loginAt);
        user.Lock("Suspicious activity", lockedAt);

        using var session = postgres.SessionFactory.OpenSession();

        using (var transaction = session.BeginTransaction())
        {
            await session.SaveAsync(user, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        session.Clear();
        var loaded = await session.GetAsync<User>(user.Id, cancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(email, loaded.Email);
        Assert.Equal("Ana Pérez", loaded.FullName);
        Assert.Equal(user.PasswordHash, loaded.PasswordHash);
        Assert.True(loaded.VerifyPassword(Password, Hasher));
        Assert.Equal(verifiedAt, loaded.EmailVerifiedAtUtc);
        Assert.Equal(UserStatus.Locked, loaded.Status);
        Assert.Equal(lockedAt, loaded.LockedAtUtc);
        Assert.Equal("Suspicious activity", loaded.LockReason);
        Assert.Equal(user.SecurityStamp, loaded.SecurityStamp);
        Assert.Equal(loginAt, loaded.LastLoginAtUtc);
        Assert.NotEqual(default, loaded.CreatedAtUtc);
        Assert.Equal(loaded.CreatedAtUtc, loaded.UpdatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, loaded.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, loaded.UpdatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, loaded.EmailVerifiedAtUtc!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, loaded.LockedAtUtc!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, loaded.LastLoginAtUtc!.Value.Kind);
    }

    [Fact]
    public async Task Update_PersistsUnlockAndPasswordChange()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var user = User.Create(UniqueEmail(), "Ana", Password, Hasher).Value;
        user.Lock(null, new DateTime(2026, 11, 19, 11, 0, 0, DateTimeKind.Utc));

        using var session = postgres.SessionFactory.OpenSession();

        using (var transaction = session.BeginTransaction())
        {
            await session.SaveAsync(user, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        session.Clear();

        using (var transaction = session.BeginTransaction())
        {
            var toUpdate = await session.GetAsync<User>(user.Id, cancellationToken);
            Assert.True(toUpdate.Unlock().IsSuccess);
            Assert.True(toUpdate.SetPassword("another strong password", Hasher).IsSuccess);
            await transaction.CommitAsync(cancellationToken);
        }

        session.Clear();
        var loaded = await session.GetAsync<User>(user.Id, cancellationToken);

        Assert.Equal(UserStatus.Active, loaded.Status);
        Assert.Null(loaded.LockedAtUtc);
        Assert.Null(loaded.LockReason);
        Assert.NotEqual(user.SecurityStamp, loaded.SecurityStamp);
        Assert.True(loaded.VerifyPassword("another strong password", Hasher));
        Assert.False(loaded.VerifyPassword(Password, Hasher));
    }

    [Fact]
    public async Task Insert_WithSameEmailInDifferentCase_IsRejectedByUniqueIndex()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = UniqueEmail();
        var first = User.Create(email, "First", Password, Hasher).Value;
        var second = User.Create(email.ToUpperInvariant(), "Second", Password, Hasher).Value;

        using (var firstSession = postgres.SessionFactory.OpenSession())
        using (var firstTransaction = firstSession.BeginTransaction())
        {
            await firstSession.SaveAsync(first, cancellationToken);
            await firstTransaction.CommitAsync(cancellationToken);
        }

        using var secondSession = postgres.SessionFactory.OpenSession();
        using var secondTransaction = secondSession.BeginTransaction();
        await secondSession.SaveAsync(second, cancellationToken);

        var exception = await Assert.ThrowsAsync<GenericADOException>(() => secondTransaction.CommitAsync(cancellationToken));

        var postgresException = PostgresExceptions.Find(exception);
        Assert.NotNull(postgresException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
        Assert.Equal(User.EmailUniqueIndexName, postgresException.ConstraintName);
    }

    [Fact]
    public async Task Insert_WithLockedStatusButNoLockTimestamp_IsRejectedByCheckConstraint()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var user = User.Create(UniqueEmail(), "Ana", Password, Hasher).Value;

        using var session = postgres.SessionFactory.OpenSession();

        using (var transaction = session.BeginTransaction())
        {
            await session.SaveAsync(user, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => session
            .CreateSQLQuery("UPDATE users SET status = 'Locked' WHERE id = :id")
            .SetParameter("id", user.Id)
            .ExecuteUpdateAsync(cancellationToken));

        var postgresException = PostgresExceptions.Find(exception);
        Assert.NotNull(postgresException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        Assert.Equal("ck_users_lock_state", postgresException.ConstraintName);
    }

    [Fact]
    public async Task Api_RegistersPasswordHasherAsSingletonUsingPbkdf2Sha512()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);

        var first = factory.Services.GetRequiredService<IPasswordHasher<User>>();
        var second = factory.Services.GetRequiredService<IPasswordHasher<User>>();

        Assert.IsType<PasswordHasher<User>>(first);
        Assert.Same(first, second);

        var user = User.Create(UniqueEmail(), "Ana", Password, first).Value;
        var header = Convert.FromBase64String(user.PasswordHash);
        const byte identityV3FormatMarker = 0x01;
        const uint hmacSha512Prf = 2;

        Assert.Equal(identityV3FormatMarker, header[0]);
        Assert.Equal(hmacSha512Prf, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1, 4)));
        Assert.Equal(100_000u, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5, 4)));
    }

    private static string UniqueEmail() => $"user-{Guid.NewGuid():N}@example.com";
}
