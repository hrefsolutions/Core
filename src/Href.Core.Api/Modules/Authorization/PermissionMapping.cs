using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;

namespace Href.Core.Api.Modules.Authorization;

public sealed class PermissionMapping : ClassMapping<Permission>
{
    public PermissionMapping()
    {
        Table("permissions");

        Id(entity => entity.Id, mapper =>
        {
            mapper.Column("id");
            mapper.Generator(Generators.Assigned);
        });

        Property(entity => entity.ApplicationId, mapper =>
        {
            mapper.Column("application_id");
            mapper.NotNullable(true);
            mapper.Update(false);
        });

        Property(entity => entity.Key, mapper =>
        {
            mapper.Column("key");
            mapper.Length(Permission.KeyMaxLength);
            mapper.NotNullable(true);
            mapper.Update(false);
        });

        Property(entity => entity.Description, mapper =>
        {
            mapper.Column("description");
            mapper.Length(Permission.DescriptionMaxLength);
            mapper.NotNullable(false);
        });

        Property(entity => entity.CreatedAtUtc, mapper =>
        {
            mapper.Column("created_at_utc");
            mapper.Type(NHibernateUtil.UtcDateTime);
            mapper.NotNullable(true);
            mapper.Update(false);
        });

        Property(entity => entity.UpdatedAtUtc, mapper =>
        {
            mapper.Column("updated_at_utc");
            mapper.Type(NHibernateUtil.UtcDateTime);
            mapper.NotNullable(true);
        });
    }
}
