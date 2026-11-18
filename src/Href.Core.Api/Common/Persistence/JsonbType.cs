using System.Data;
using System.Data.Common;
using NHibernate.Engine;
using NHibernate.SqlTypes;
using NHibernate.UserTypes;
using Npgsql;
using NpgsqlTypes;

namespace Href.Core.Api.Common.Persistence;

/// <summary>
/// NHibernate <see cref="IUserType"/> that persists JSON strings as PostgreSQL <c>jsonb</c> columns.
/// </summary>
public sealed class JsonbType : IUserType
{
    public SqlType[] SqlTypes => [new SqlType(DbType.String)];

    public Type ReturnedType => typeof(string);

    public bool IsMutable => false;

    public new bool Equals(object? x, object? y) => string.Equals(x as string, y as string, StringComparison.Ordinal);

    public int GetHashCode(object? x) => x?.GetHashCode() ?? 0;

    public object? NullSafeGet(DbDataReader rs, string[] names, ISessionImplementor session, object owner)
    {
        var ordinal = rs.GetOrdinal(names[0]);
        return rs.IsDBNull(ordinal) ? null : rs.GetString(ordinal);
    }

    public void NullSafeSet(DbCommand cmd, object? value, int index, ISessionImplementor session)
    {
        var parameter = cmd.Parameters[index];
        parameter.Value = value ?? DBNull.Value;

        if (parameter is NpgsqlParameter npgsqlParameter)
        {
            npgsqlParameter.NpgsqlDbType = NpgsqlDbType.Jsonb;
        }
    }

    public object? DeepCopy(object? value) => value;

    public object? Replace(object? original, object? target, object owner) => original;

    public object? Assemble(object? cached, object owner) => cached;

    public object? Disassemble(object? value) => value;
}
