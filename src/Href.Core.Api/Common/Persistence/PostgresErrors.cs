using Npgsql;

namespace Href.Core.Api.Common.Persistence;

public static class PostgresErrors
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="exception"/> or one of its inner exceptions is a PostgreSQL unique violation
    /// (<c>23505</c>) on the constraint or index named <paramref name="constraintName"/>.
    /// </summary>
    public static bool IsUniqueViolation(Exception exception, string constraintName)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgresException
                && string.Equals(postgresException.ConstraintName, constraintName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
