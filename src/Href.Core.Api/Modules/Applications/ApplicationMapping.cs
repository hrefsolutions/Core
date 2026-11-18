using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using NHibernate.Type;

namespace Href.Core.Api.Modules.Applications;

public sealed class ApplicationMapping : ClassMapping<Application>
{
    public ApplicationMapping()
    {
        Table("applications");

        Id(entity => entity.Id, mapper =>
        {
            mapper.Column("id");
            mapper.Generator(Generators.Assigned);
        });

        Property(entity => entity.AppKey, mapper =>
        {
            mapper.Column("app_key");
            mapper.Length(Application.AppKeyMaxLength);
            mapper.NotNullable(true);
            mapper.Unique(true);
            mapper.Update(false);
        });

        Property(entity => entity.Name, mapper =>
        {
            mapper.Column("name");
            mapper.Length(Application.NameMaxLength);
            mapper.NotNullable(true);
        });

        Property(entity => entity.Description, mapper =>
        {
            mapper.Column("description");
            mapper.Length(Application.DescriptionMaxLength);
            mapper.NotNullable(false);
        });

        Property(entity => entity.Status, mapper =>
        {
            mapper.Column("status");
            mapper.Type<EnumStringType<ApplicationStatus>>();
            mapper.Length(20);
            mapper.NotNullable(true);
        });

        Property(entity => entity.ClientSecretHash, mapper =>
        {
            mapper.Column("client_secret_hash");
            mapper.Length(Application.SecretHashLength);
            mapper.NotNullable(true);
        });

        Property(entity => entity.ClientSecretRotatedAtUtc, mapper =>
        {
            mapper.Column("client_secret_rotated_at_utc");
            mapper.Type(NHibernateUtil.UtcDateTime);
            mapper.NotNullable(true);
        });

        Property(entity => entity.AllowSelfRegistration, mapper =>
        {
            mapper.Column("allow_self_registration");
            mapper.NotNullable(true);
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
