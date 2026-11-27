using Npgsql;

namespace Href.Core.Api.Common.Persistence;

public static class PostgresErrors
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="exception"/> or one of its inner exceptions is a PostgreSQL unique violation
    /// (<c>23505</c>) on the constraint or index named <paramref name="constraintName"/>.
    /// </summary>
    public static bool IsUniqueViolation(Exception exception, string constraintName) =>
        IsViolation(exception, PostgresErrorCodes.UniqueViolation, constraintName);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="exception"/> or one of its inner exceptions is a PostgreSQL foreign key
    /// violation (<c>23503</c>) on the constraint named <paramref name="constraintName"/>.
    /// </summary>
    public static bool IsForeignKeyViolation(Exception exception, string constraintName) =>
        IsViolation(exception, PostgresErrorCodes.ForeignKeyViolation, constraintName);

    private static bool IsViolation(Exception exception, string sqlState, string constraintName)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgresException
                && string.Equals(postgresException.SqlState, sqlState, StringComparison.Ordinal)
                && string.Equals(postgresException.ConstraintName, constraintName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
