using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using NHibernate.Type;

namespace Href.Core.Api.Modules.Users;

public sealed class UserMapping : ClassMapping<User>
{
    public UserMapping()
    {
        Table("users");

        Id(entity => entity.Id, mapper =>
        {
            mapper.Column("id");
            mapper.Generator(Generators.Assigned);
        });

        Property(entity => entity.Email, mapper =>
        {
            mapper.Column("email");
            mapper.Length(User.EmailMaxLength);
            mapper.NotNullable(true);
            mapper.Unique(true);
        });

        Property(entity => entity.FullName, mapper =>
        {
            mapper.Column("full_name");
            mapper.Length(User.FullNameMaxLength);
            mapper.NotNullable(true);
        });

        Property(entity => entity.PasswordHash, mapper =>
        {
            mapper.Column("password_hash");
            mapper.Length(User.PasswordHashMaxLength);
            mapper.NotNullable(true);
        });

        Property(entity => entity.EmailVerifiedAtUtc, mapper =>
        {
            mapper.Column("email_verified_at_utc");
            mapper.Type(NHibernateUtil.UtcDateTime);
            mapper.NotNullable(false);
        });

        Property(entity => entity.Status, mapper =>
        {
            mapper.Column("status");
            mapper.Type<EnumStringType<UserStatus>>();
            mapper.Length(20);
            mapper.NotNullable(true);
        });

        Property(entity => entity.LockedAtUtc, mapper =>
        {
            mapper.Column("locked_at_utc");
            mapper.Type(NHibernateUtil.UtcDateTime);
            mapper.NotNullable(false);
        });

        Property(entity => entity.LockReason, mapper =>
        {
            mapper.Column("lock_reason");
            mapper.Length(User.LockReasonMaxLength);
            mapper.NotNullable(false);
        });

        Property(entity => entity.SecurityStamp, mapper =>
        {
            mapper.Column("security_stamp");
            mapper.NotNullable(true);
        });

        Property(entity => entity.LastLoginAtUtc, mapper =>
        {
            mapper.Column("last_login_at_utc");
            mapper.Type(NHibernateUtil.UtcDateTime);
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
