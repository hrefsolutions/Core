using Href.Core.Api.Common.Persistence;
using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using NHibernate.Type;

namespace Href.Core.Api.Modules.Audit;

public sealed class AuditLogMapping : ClassMapping<AuditLog>
{
    public AuditLogMapping()
    {
        Table("audit_logs");
        Mutable(false);

        Id(entity => entity.Id, mapper =>
        {
            mapper.Column("id");
            mapper.Generator(Generators.Assigned);
        });

        Property(entity => entity.OccurredAtUtc, mapper =>
        {
            mapper.Column("occurred_at_utc");
            mapper.Type(NHibernateUtil.UtcDateTime);
            mapper.NotNullable(true);
            mapper.Update(false);
        });

        Property(entity => entity.Action, mapper =>
        {
            mapper.Column("action");
            mapper.Length(AuditLog.ActionMaxLength);
            mapper.NotNullable(true);
            mapper.Update(false);
        });

        Property(entity => entity.EntityType, mapper =>
        {
            mapper.Column("entity_type");
            mapper.Length(AuditLog.EntityTypeMaxLength);
            mapper.NotNullable(false);
            mapper.Update(false);
        });

        Property(entity => entity.EntityId, mapper =>
        {
            mapper.Column("entity_id");
            mapper.Length(AuditLog.EntityIdMaxLength);
            mapper.NotNullable(false);
            mapper.Update(false);
        });

        Property(entity => entity.ActorType, mapper =>
        {
            mapper.Column("actor_type");
            mapper.Type<EnumStringType<AuditActorType>>();
            mapper.Length(AuditLog.ActorTypeMaxLength);
            mapper.NotNullable(true);
            mapper.Update(false);
        });

        Property(entity => entity.ActorId, mapper =>
        {
            mapper.Column("actor_id");
            mapper.Length(AuditLog.ActorIdMaxLength);
            mapper.NotNullable(false);
            mapper.Update(false);
        });

        Property(entity => entity.AppKey, mapper =>
        {
            mapper.Column("app_key");
            mapper.Length(AuditLog.AppKeyMaxLength);
            mapper.NotNullable(false);
            mapper.Update(false);
        });

        Property(entity => entity.OldValues, mapper =>
        {
            mapper.Column("old_values");
            mapper.Type<JsonbType>();
            mapper.NotNullable(false);
            mapper.Update(false);
        });

        Property(entity => entity.NewValues, mapper =>
        {
            mapper.Column("new_values");
            mapper.Type<JsonbType>();
            mapper.NotNullable(false);
            mapper.Update(false);
        });

        Property(entity => entity.CorrelationId, mapper =>
        {
            mapper.Column("correlation_id");
            mapper.Length(AuditLog.CorrelationIdMaxLength);
            mapper.NotNullable(false);
            mapper.Update(false);
        });

        Property(entity => entity.IpAddress, mapper =>
        {
            mapper.Column("ip_address");
            mapper.Length(AuditLog.IpAddressMaxLength);
            mapper.NotNullable(false);
            mapper.Update(false);
        });

        Property(entity => entity.UserAgent, mapper =>
        {
            mapper.Column("user_agent");
            mapper.Length(AuditLog.UserAgentMaxLength);
            mapper.NotNullable(false);
            mapper.Update(false);
        });
    }
}
