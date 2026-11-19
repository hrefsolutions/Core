using Npgsql;

namespace Href.Core.Tests.Integration;

internal static class PostgresExceptions
{
    public static PostgresException? Find(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is PostgresException postgresException)
            {
                return postgresException;
            }

            exception = exception.InnerException;
        }

        return null;
    }
}
