using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;

namespace Href.Core.Api.Modules.Notifications;

public sealed class EmailTemplateMapping : ClassMapping<EmailTemplate>
{
    public EmailTemplateMapping()
    {
        Table("email_templates");

        Id(entity => entity.Id, mapper =>
        {
            mapper.Column("id");
            mapper.Generator(Generators.Assigned);
        });

        Property(entity => entity.Key, mapper =>
        {
            mapper.Column("key");
            mapper.Length(EmailTemplate.KeyMaxLength);
            mapper.NotNullable(true);
            mapper.Unique(true);
            mapper.Update(false);
        });

        Property(entity => entity.Name, mapper =>
        {
            mapper.Column("name");
            mapper.Length(EmailTemplate.NameMaxLength);
            mapper.NotNullable(true);
        });

        Property(entity => entity.Description, mapper =>
        {
            mapper.Column("description");
            mapper.Length(EmailTemplate.DescriptionMaxLength);
            mapper.NotNullable(false);
        });

        Property(entity => entity.Subject, mapper =>
        {
            mapper.Column("subject");
            mapper.Length(EmailTemplate.SubjectMaxLength);
            mapper.NotNullable(true);
        });

        Property(entity => entity.HtmlBody, mapper =>
        {
            mapper.Column("html_body");
            mapper.Type(NHibernateUtil.StringClob);
            mapper.Length(int.MaxValue);
            mapper.NotNullable(true);
        });

        Property(entity => entity.TextBody, mapper =>
        {
            mapper.Column("text_body");
            mapper.Type(NHibernateUtil.StringClob);
            mapper.Length(int.MaxValue);
            mapper.NotNullable(false);
        });

        Property(entity => entity.IsSystem, mapper =>
        {
            mapper.Column("is_system");
            mapper.NotNullable(true);
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
