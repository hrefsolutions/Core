CREATE TABLE email_templates (
    id UUID PRIMARY KEY,
    key VARCHAR(100) NOT NULL,
    name VARCHAR(100) NOT NULL,
    description VARCHAR(500) NULL,
    subject VARCHAR(500) NOT NULL,
    html_body TEXT NOT NULL,
    text_body TEXT NULL,
    is_system BOOLEAN NOT NULL DEFAULT FALSE,
    created_at_utc TIMESTAMPTZ NOT NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL
);

CREATE UNIQUE INDEX ux_email_templates_key ON email_templates (key);

CREATE TABLE notifications (
    id UUID PRIMARY KEY,
    app_key VARCHAR(50) NOT NULL,
    to_email VARCHAR(254) NOT NULL,
    to_name VARCHAR(200) NULL,
    subject VARCHAR(500) NOT NULL,
    html_body TEXT NOT NULL,
    text_body TEXT NULL,
    template_key VARCHAR(100) NULL,
    status VARCHAR(20) NOT NULL,
    attempts INTEGER NOT NULL DEFAULT 0,
    max_attempts INTEGER NOT NULL,
    version INTEGER NOT NULL DEFAULT 1,
    next_attempt_at_utc TIMESTAMPTZ NOT NULL,
    processing_started_at_utc TIMESTAMPTZ NULL,
    sent_at_utc TIMESTAMPTZ NULL,
    last_error VARCHAR(2000) NULL,
    notify_on_failure_to VARCHAR(254) NULL,
    correlation_id VARCHAR(64) NULL,
    created_at_utc TIMESTAMPTZ NOT NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_notifications_status CHECK (status IN ('Pending', 'Processing', 'Sent', 'Failed')),
    CONSTRAINT ck_notifications_attempts CHECK (attempts >= 0),
    CONSTRAINT ck_notifications_max_attempts CHECK (max_attempts > 0)
);

CREATE INDEX ix_notifications_status_next_attempt_at_utc ON notifications (status, next_attempt_at_utc);
CREATE INDEX ix_notifications_app_key_created_at_utc ON notifications (app_key, created_at_utc DESC);
