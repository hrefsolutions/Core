using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Auth;
using Href.Core.Api.Modules.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NHibernate;
using NHibernate.Linq;
using Npgsql;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RefreshTokenPersistenceIntegrationTests(PostgresFixture postgres)
{
    private static readonly DateTime Now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private static readonly PasswordHasher<User> Hasher = new(
        Options.Create(new PasswordHasherOptions { IterationCount = 1_000 }));

    [Fact]
    public async Task InsertAndLoad_RoundTripsAllProperties()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (user, application) = await SaveUserAndApplicationAsync(cancellationToken);
        var issued = RefreshToken.Issue(user.Id, application.Id, Now, Lifetime, "203.0.113.7", "Satellite/1.0");
        issued.Token.Revoke(Now.AddMinutes(5), RefreshTokenRevocationReason.Logout);
        await SaveAllAsync(cancellationToken, issued.Token);

        using var session = postgres.SessionFactory.OpenSession();
        var loaded = await session.GetAsync<RefreshToken>(issued.Token.Id, cancellationToken);

        Assert.Equal(user.Id, loaded.UserId);
        Assert.Equal(application.Id, loaded.ApplicationId);
        Assert.Equal(issued.Token.FamilyId, loaded.FamilyId);
        Assert.Equal(RefreshToken.HashToken(issued.PlainToken), loaded.TokenHash);
        Assert.Equal(Now, loaded.CreatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, loaded.CreatedAtUtc.Kind);
        Assert.Equal(Now + Lifetime, loaded.ExpiresAtUtc);
        Assert.Equal(DateTimeKind.Utc, loaded.ExpiresAtUtc.Kind);
        Assert.Equal(Now.AddMinutes(5), loaded.RevokedAtUtc);
        Assert.Equal(DateTimeKind.Utc, loaded.RevokedAtUtc!.Value.Kind);
        Assert.Equal(RefreshTokenRevocationReason.Logout, loaded.RevokedReason);
        Assert.Null(loaded.ReplacedByTokenId);
        Assert.Equal("203.0.113.7", loaded.CreatedByIp);
        Assert.Equal("Satellite/1.0", loaded.UserAgent);
    }

    [Fact]
    public async Task InsertAndLoad_StoresTheReasonAsItsName()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (user, application) = await SaveUserAndApplicationAsync(cancellationToken);
        var token = RefreshToken.Issue(user.Id, application.Id, Now, Lifetime, null, null).Token;
        token.Revoke(Now.AddMinutes(1), RefreshTokenRevocationReason.ReuseDetected);
        await SaveAllAsync(cancellationToken, token);

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT revoked_reason FROM refresh_tokens WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", token.Id);

        Assert.Equal("ReuseDetected", await command.ExecuteScalarAsync(cancellationToken));
    }

    [Fact]
    public async Task Rotate_InOneTransaction_PersistsTheRevokedTokenAndItsSuccessor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (user, application) = await SaveUserAndApplicationAsync(cancellationToken);
        var issued = RefreshToken.Issue(user.Id, application.Id, Now, Lifetime, null, null);
        await SaveAllAsync(cancellationToken, issued.Token);

        Guid successorId;

        using (var session = postgres.SessionFactory.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var hash = RefreshToken.HashToken(issued.PlainToken);
            var current = await session.Query<RefreshToken>().SingleAsync(token => token.TokenHash == hash, cancellationToken);
            var successor = current.Rotate(Now.AddDays(1), Lifetime, null, null).Value.Token;
            await session.SaveAsync(successor, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            successorId = successor.Id;
        }

        using var verify = postgres.SessionFactory.OpenSession();
        var rotated = await verify.GetAsync<RefreshToken>(issued.Token.Id, cancellationToken);
        var loadedSuccessor = await verify.GetAsync<RefreshToken>(successorId, cancellationToken);
        Assert.Equal(RefreshTokenRevocationReason.Rotated, rotated.RevokedReason);
        Assert.Equal(Now.AddDays(1), rotated.RevokedAtUtc);
        Assert.Equal(successorId, rotated.ReplacedByTokenId);
        Assert.Equal(issued.Token.FamilyId, loadedSuccessor.FamilyId);
        Assert.Null(loadedSuccessor.RevokedReason);
        Assert.Equal(Now.AddDays(1) + Lifetime, loadedSuccessor.ExpiresAtUtc);
    }

    [Fact]
    public async Task ConcurrentRotations_SecondCommitFailsWithStaleStateAndOnlyOneSuccessorExists()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (user, application) = await SaveUserAndApplicationAsync(cancellationToken);
        var token = RefreshToken.Issue(user.Id, application.Id, Now, Lifetime, null, null).Token;
        await SaveAllAsync(cancellationToken, token);

        using var firstSession = postgres.SessionFactory.OpenSession();
        using var secondSession = postgres.SessionFactory.OpenSession();
        using var firstTransaction = firstSession.BeginTransaction();
        using var secondTransaction = secondSession.BeginTransaction();
        var firstCopy = await firstSession.GetAsync<RefreshToken>(token.Id, cancellationToken);
        var secondCopy = await secondSession.GetAsync<RefreshToken>(token.Id, cancellationToken);
        var firstSuccessor = firstCopy.Rotate(Now.AddMinutes(1), Lifetime, null, null).Value.Token;
        var secondSuccessor = secondCopy.Rotate(Now.AddMinutes(1), Lifetime, null, null).Value.Token;
        await firstSession.SaveAsync(firstSuccessor, cancellationToken);
        await secondSession.SaveAsync(secondSuccessor, cancellationToken);

        await firstTransaction.CommitAsync(cancellationToken);
        var exception = await Record.ExceptionAsync(() => secondTransaction.CommitAsync(cancellationToken));

        Assert.NotNull(exception);
        Assert.True(ContainsStaleObjectState(exception), exception.ToString());
        using var verify = postgres.SessionFactory.OpenSession();
        var family = await verify.Query<RefreshToken>()
            .Where(candidate => candidate.FamilyId == token.FamilyId)
            .ToListAsync(cancellationToken);
        Assert.Equal(new[] { token.Id, firstSuccessor.Id }.Order(), family.Select(candidate => candidate.Id).Order());
        Assert.Equal(firstSuccessor.Id, family.Single(candidate => candidate.Id == token.Id).ReplacedByTokenId);
    }

    [Fact]
    public async Task ReuseDetected_RevokesTheWholeFamilyLoadedByFamilyId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (user, application) = await SaveUserAndApplicationAsync(cancellationToken);
        var first = RefreshToken.Issue(user.Id, application.Id, Now, Lifetime, null, null).Token;
        var second = first.Rotate(Now.AddMinutes(1), Lifetime, null, null).Value.Token;
        var third = second.Rotate(Now.AddMinutes(2), Lifetime, null, null).Value.Token;
        var otherFamily = RefreshToken.Issue(user.Id, application.Id, Now, Lifetime, null, null).Token;
        await SaveAllAsync(cancellationToken, third, second, first, otherFamily);

        using (var session = postgres.SessionFactory.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var presented = await session.GetAsync<RefreshToken>(first.Id, cancellationToken);
            Assert.Equal(AuthErrors.RefreshTokenReused, presented.Rotate(Now.AddMinutes(3), Lifetime, null, null).Error);
            var family = await session.Query<RefreshToken>()
                .Where(token => token.FamilyId == presented.FamilyId)
                .ToListAsync(cancellationToken);
            Assert.Equal(3, family.Count);
            Assert.Equal(1, RefreshToken.RevokeFamily(family, Now.AddMinutes(3), RefreshTokenRevocationReason.ReuseDetected));
            await transaction.CommitAsync(cancellationToken);
        }

        using var verify = postgres.SessionFactory.OpenSession();
        Assert.Equal(RefreshTokenRevocationReason.ReuseDetected, (await verify.GetAsync<RefreshToken>(third.Id, cancellationToken)).RevokedReason);
        Assert.Equal(RefreshTokenRevocationReason.Rotated, (await verify.GetAsync<RefreshToken>(first.Id, cancellationToken)).RevokedReason);
        Assert.Null((await verify.GetAsync<RefreshToken>(otherFamily.Id, cancellationToken)).RevokedReason);
    }

    [Fact]
    public async Task DuplicateTokenHash_IsRejectedByTheUniqueIndex()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (user, application) = await SaveUserAndApplicationAsync(cancellationToken);
        var token = RefreshToken.Issue(user.Id, application.Id, Now, Lifetime, null, null).Token;
        await SaveAllAsync(cancellationToken, token);

        var exception = await Record.ExceptionAsync(() => InsertRawAsync(
            Guid.CreateVersion7(), user.Id, application.Id, token.TokenHash, revokedAtUtc: null, revokedReason: null, replacedBy: null, cancellationToken));

        var postgresException = PostgresExceptions.Find(exception);
        Assert.NotNull(postgresException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
        Assert.Equal("ux_refresh_tokens_token_hash", postgresException.ConstraintName);
    }

    [Fact]
    public async Task UnknownUser_IsRejectedByTheForeignKey()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (_, application) = await SaveUserAndApplicationAsync(cancellationToken);
        var token = RefreshToken.Issue(Guid.CreateVersion7(), application.Id, Now, Lifetime, null, null).Token;

        var exception = await Record.ExceptionAsync(() => SaveAllAsync(cancellationToken, token));

        var postgresException = PostgresExceptions.Find(exception);
        Assert.NotNull(postgresException);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgresException.SqlState);
    }

    [Fact]
    public async Task UnknownApplication_IsRejectedByTheForeignKey()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (user, _) = await SaveUserAndApplicationAsync(cancellationToken);
        var token = RefreshToken.Issue(user.Id, Guid.CreateVersion7(), Now, Lifetime, null, null).Token;

        var exception = await Record.ExceptionAsync(() => SaveAllAsync(cancellationToken, token));

        var postgresException = PostgresExceptions.Find(exception);
        Assert.NotNull(postgresException);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgresException.SqlState);
    }

    [Theory]
    [InlineData(true, null, false, "ck_refresh_tokens_revocation")]
    [InlineData(false, "Logout", false, "ck_refresh_tokens_revocation")]
    [InlineData(true, "Expired", false, "ck_refresh_tokens_revoked_reason")]
    [InlineData(true, "Logout", true, "ck_refresh_tokens_replaced_by")]
    public async Task InconsistentRevocationState_IsRejectedByCheckConstraints(
        bool revoked,
        string? reason,
        bool withReplacement,
        string expectedConstraint)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (user, application) = await SaveUserAndApplicationAsync(cancellationToken);
        var existing = RefreshToken.Issue(user.Id, application.Id, Now, Lifetime, null, null).Token;
        await SaveAllAsync(cancellationToken, existing);

        var exception = await Record.ExceptionAsync(() => InsertRawAsync(
            Guid.CreateVersion7(),
            user.Id,
            application.Id,
            new string('a', 64),
            revoked ? Now : null,
            reason,
            withReplacement ? existing.Id : null,
            cancellationToken));

        var postgresException = PostgresExceptions.Find(exception);
        Assert.NotNull(postgresException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        Assert.Equal(expectedConstraint, postgresException.ConstraintName);
    }

    private static bool ContainsStaleObjectState(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is StaleObjectStateException)
            {
                return true;
            }

            exception = exception.InnerException;
        }

        return false;
    }

    private async Task InsertRawAsync(
        Guid id,
        Guid userId,
        Guid applicationId,
        string tokenHash,
        DateTime? revokedAtUtc,
        string? revokedReason,
        Guid? replacedBy,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO refresh_tokens (id, user_id, application_id, family_id, token_hash, created_at_utc, expires_at_utc,
                                        revoked_at_utc, revoked_reason, replaced_by_token_id)
            VALUES (@id, @userId, @applicationId, @familyId, @tokenHash, @createdAt, @expiresAt, @revokedAt, @revokedReason, @replacedBy)
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("applicationId", applicationId);
        command.Parameters.AddWithValue("familyId", Guid.CreateVersion7());
        command.Parameters.AddWithValue("tokenHash", tokenHash);
        command.Parameters.AddWithValue("createdAt", Now);
        command.Parameters.AddWithValue("expiresAt", Now + Lifetime);
        command.Parameters.Add(new NpgsqlParameter<DateTime?>("revokedAt", revokedAtUtc));
        command.Parameters.Add(new NpgsqlParameter<string?>("revokedReason", revokedReason));
        command.Parameters.Add(new NpgsqlParameter<Guid?>("replacedBy", replacedBy));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SaveAllAsync(CancellationToken cancellationToken, params object[] entities)
    {
        using var session = postgres.SessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();

        foreach (var entity in entities)
        {
            await session.SaveAsync(entity, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<(User User, Application Application)> SaveUserAndApplicationAsync(CancellationToken cancellationToken)
    {
        var application = Application.Create($"rt-{Guid.NewGuid():N}"[..20], "Refresh App", null, false, TimeProvider.System).Value.Application;
        var user = User.Create($"rt-{Guid.NewGuid():N}@example.com", "Refresh User", "correct horse battery", Hasher).Value;
        await SaveAllAsync(cancellationToken, application, user);
        return (user, application);
    }
}
