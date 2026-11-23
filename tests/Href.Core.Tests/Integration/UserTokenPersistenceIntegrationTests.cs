using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Auth;
using Href.Core.Api.Modules.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class UserTokenPersistenceIntegrationTests(PostgresFixture postgres)
{
    private static readonly DateTime Now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    private static readonly PasswordHasher<User> Hasher = new(
        Options.Create(new PasswordHasherOptions { IterationCount = 1_000 }));

    [Fact]
    public async Task InsertAndLoad_RoundTripsAllProperties()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (user, application) = await SaveUserAndApplicationAsync(cancellationToken);
        var issued = UserToken.Issue(user.Id, application.Id, UserTokenPurpose.PasswordReset, Now, UserToken.PasswordResetLifetime);
        issued.Token.Consume(Now.AddMinutes(5));
        await SaveAllAsync(cancellationToken, issued.Token);

        using var session = postgres.SessionFactory.OpenSession();
        var loaded = await session.GetAsync<UserToken>(issued.Token.Id, cancellationToken);

        Assert.Equal(user.Id, loaded.UserId);
        Assert.Equal(application.Id, loaded.ApplicationId);
        Assert.Equal(UserTokenPurpose.PasswordReset, loaded.Purpose);
        Assert.Equal(UserToken.HashToken(issued.PlainToken), loaded.TokenHash);
        Assert.Equal(Now, loaded.CreatedAtUtc);
        Assert.Equal(Now.AddMinutes(30), loaded.ExpiresAtUtc);
        Assert.Equal(Now.AddMinutes(5), loaded.ConsumedAtUtc);
        Assert.Equal(DateTimeKind.Utc, loaded.ConsumedAtUtc!.Value.Kind);
        Assert.Null(loaded.RevokedAtUtc);
    }

    [Fact]
    public async Task DuplicateTokenHash_IsRejectedByTheUniqueIndex()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (user, application) = await SaveUserAndApplicationAsync(cancellationToken);
        var token = UserToken.Issue(user.Id, application.Id, UserTokenPurpose.EmailVerification, Now, UserToken.EmailVerificationLifetime).Token;
        await SaveAllAsync(cancellationToken, token);

        var exception = await Record.ExceptionAsync(() => InsertRawAsync(user.Id, application.Id, "EmailVerification", token.TokenHash, consumed: false, revoked: false, cancellationToken));

        var postgresException = PostgresExceptions.Find(exception);
        Assert.NotNull(postgresException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
        Assert.Equal(UserToken.TokenHashUniqueIndexName, postgresException.ConstraintName);
    }

    [Theory]
    [InlineData("Login", false, false, "ck_user_tokens_purpose")]
    [InlineData("EmailVerification", true, true, "ck_user_tokens_single_end")]
    public async Task InconsistentRows_AreRejectedByCheckConstraints(string purpose, bool consumed, bool revoked, string expectedConstraint)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (user, application) = await SaveUserAndApplicationAsync(cancellationToken);

        var exception = await Record.ExceptionAsync(() => InsertRawAsync(user.Id, application.Id, purpose, new string('b', 64), consumed, revoked, cancellationToken));

        var postgresException = PostgresExceptions.Find(exception);
        Assert.NotNull(postgresException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        Assert.Equal(expectedConstraint, postgresException.ConstraintName);
    }

    [Fact]
    public async Task Indexes_AreTheUniqueHashAndThePartialOpenTokensIndex()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT indexname, indexdef FROM pg_indexes WHERE tablename = 'user_tokens' AND indexname <> 'user_tokens_pkey'",
            connection);
        var indexes = new Dictionary<string, string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                indexes[reader.GetString(0)] = reader.GetString(1);
            }
        }

        Assert.Equal(
            ["ix_user_tokens_user_id_purpose_open", UserToken.TokenHashUniqueIndexName],
            indexes.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("(user_id, purpose) WHERE ((consumed_at_utc IS NULL) AND (revoked_at_utc IS NULL))", indexes["ix_user_tokens_user_id_purpose_open"], StringComparison.Ordinal);
    }

    private async Task InsertRawAsync(
        Guid userId,
        Guid applicationId,
        string purpose,
        string tokenHash,
        bool consumed,
        bool revoked,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO user_tokens (id, user_id, application_id, purpose, token_hash, created_at_utc, expires_at_utc, consumed_at_utc, revoked_at_utc)
            VALUES (@id, @userId, @applicationId, @purpose, @tokenHash, @createdAt, @expiresAt, @consumedAt, @revokedAt)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("applicationId", applicationId);
        command.Parameters.AddWithValue("purpose", purpose);
        command.Parameters.AddWithValue("tokenHash", tokenHash);
        command.Parameters.AddWithValue("createdAt", Now);
        command.Parameters.AddWithValue("expiresAt", Now.AddHours(1));
        command.Parameters.Add(new NpgsqlParameter<DateTime?>("consumedAt", consumed ? Now : null));
        command.Parameters.Add(new NpgsqlParameter<DateTime?>("revokedAt", revoked ? Now : null));
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
        var application = Application.Create($"ut-{Guid.NewGuid():N}"[..20], "Token App", null, false, TimeProvider.System).Value.Application;
        var user = User.Create($"ut-{Guid.NewGuid():N}@example.com", "Token User", "correct horse battery", Hasher).Value;
        await SaveAllAsync(cancellationToken, application, user);
        return (user, application);
    }
}
