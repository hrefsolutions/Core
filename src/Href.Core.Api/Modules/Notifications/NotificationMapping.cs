using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using NHibernate.Type;

namespace Href.Core.Api.Modules.Notifications;

public sealed class NotificationMapping : ClassMapping<Notification>
{
    public NotificationMapping()
    {
        Table("notifications");

        Id(entity => entity.Id, mapper =>
        {
            mapper.Column("id");
            mapper.Generator(Generators.Assigned);
        });

        Version(entity => entity.Version, mapper =>
        {
            mapper.Column("version");
            mapper.UnsavedValue(0);
        });

        Property(entity => entity.AppKey, mapper =>
        {
            mapper.Column("app_key");
            mapper.Length(Notification.AppKeyMaxLength);
            mapper.NotNullable(true);
            mapper.Update(false);
        });

        Property(entity => entity.ToEmail, mapper =>
        {
            mapper.Column("to_email");
            mapper.Length(Notification.EmailMaxLength);
            mapper.NotNullable(true);
            mapper.Update(false);
        });

        Property(entity => entity.ToName, mapper =>
        {
            mapper.Column("to_name");
            mapper.Length(Notification.RecipientNameMaxLength);
            mapper.NotNullable(false);
            mapper.Update(false);
        });

        Property(entity => entity.Subject, mapper =>
        {
            mapper.Column("subject");
            mapper.Length(Notification.SubjectMaxLength);
            mapper.NotNullable(true);
            mapper.Update(false);
        });

        Property(entity => entity.HtmlBody, mapper =>
        {
            mapper.Column("html_body");
            mapper.Type(NHibernateUtil.StringClob);
            mapper.Length(int.MaxValue);
            mapper.NotNullable(true);
            mapper.Update(false);
        });

        Property(entity => entity.TextBody, mapper =>
        {
            mapper.Column("text_body");
            mapper.Type(NHibernateUtil.StringClob);
            mapper.Length(int.MaxValue);
            mapper.NotNullable(false);
            mapper.Update(false);
        });

        Property(entity => entity.TemplateKey, mapper =>
        {
            mapper.Column("template_key");
            mapper.Length(Notification.TemplateKeyMaxLength);
            mapper.NotNullable(false);
            mapper.Update(false);
        });

        Property(entity => entity.Status, mapper =>
        {
            mapper.Column("status");
            mapper.Type<EnumStringType<NotificationStatus>>();
            mapper.Length(20);
            mapper.NotNullable(true);
        });

        Property(entity => entity.Attempts, mapper =>
        {
            mapper.Column("attempts");
            mapper.NotNullable(true);
        });

        Property(entity => entity.MaxAttempts, mapper =>
        {
            mapper.Column("max_attempts");
            mapper.NotNullable(true);
            mapper.Update(false);
        });

        Property(entity => entity.NextAttemptAtUtc, mapper =>
        {
            mapper.Column("next_attempt_at_utc");
            mapper.Type(NHibernateUtil.UtcDateTime);
            mapper.NotNullable(true);
        });

        Property(entity => entity.ProcessingStartedAtUtc, mapper =>
        {
            mapper.Column("processing_started_at_utc");
            mapper.Type(NHibernateUtil.UtcDateTime);
            mapper.NotNullable(false);
        });

        Property(entity => entity.SentAtUtc, mapper =>
        {
            mapper.Column("sent_at_utc");
            mapper.Type(NHibernateUtil.UtcDateTime);
            mapper.NotNullable(false);
        });

        Property(entity => entity.LastError, mapper =>
        {
            mapper.Column("last_error");
            mapper.Length(Notification.LastErrorMaxLength);
            mapper.NotNullable(false);
        });

        Property(entity => entity.NotifyOnFailureTo, mapper =>
        {
            mapper.Column("notify_on_failure_to");
            mapper.Length(Notification.EmailMaxLength);
            mapper.NotNullable(false);
            mapper.Update(false);
        });

        Property(entity => entity.CorrelationId, mapper =>
        {
            mapper.Column("correlation_id");
            mapper.Length(Notification.CorrelationIdMaxLength);
            mapper.NotNullable(false);
            mapper.Update(false);
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
