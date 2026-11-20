using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;

namespace Href.Core.Api.Modules.Authorization;

public sealed class UserApplicationAccessMapping : ClassMapping<UserApplicationAccess>
{
    public UserApplicationAccessMapping()
    {
        Table("user_application_accesses");

        Id(entity => entity.Id, mapper =>
        {
            mapper.Column("id");
            mapper.Generator(Generators.Assigned);
        });

        Property(entity => entity.UserId, mapper =>
        {
            mapper.Column("user_id");
            mapper.NotNullable(true);
            mapper.Update(false);
        });

        Property(entity => entity.ApplicationId, mapper =>
        {
            mapper.Column("application_id");
            mapper.NotNullable(true);
            mapper.Update(false);
        });

        Set(entity => entity.Roles, mapper =>
        {
            mapper.Access(Accessor.Field);
            mapper.Table("user_application_access_roles");
            mapper.Key(key => key.Column("user_application_access_id"));
            mapper.Cascade(Cascade.None);
            mapper.Lazy(CollectionLazy.Lazy);
        }, relation => relation.ManyToMany(manyToMany => manyToMany.Column("role_id")));

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
