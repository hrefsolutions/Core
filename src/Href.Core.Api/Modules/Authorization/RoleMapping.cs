using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;

namespace Href.Core.Api.Modules.Authorization;

public sealed class RoleMapping : ClassMapping<Role>
{
    public RoleMapping()
    {
        Table("roles");

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

        Property(entity => entity.Name, mapper =>
        {
            mapper.Column("name");
            mapper.Length(Role.NameMaxLength);
            mapper.NotNullable(true);
        });

        Property(entity => entity.Description, mapper =>
        {
            mapper.Column("description");
            mapper.Length(Role.DescriptionMaxLength);
            mapper.NotNullable(false);
        });

        Property(entity => entity.IsDefault, mapper =>
        {
            mapper.Column("is_default");
            mapper.NotNullable(true);
        });

        Set(entity => entity.Permissions, mapper =>
        {
            mapper.Access(Accessor.Field);
            mapper.Table("role_permissions");
            mapper.Key(key => key.Column("role_id"));
            mapper.Cascade(Cascade.None);
            mapper.Lazy(CollectionLazy.Lazy);
        }, relation => relation.ManyToMany(manyToMany => manyToMany.Column("permission_id")));

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
