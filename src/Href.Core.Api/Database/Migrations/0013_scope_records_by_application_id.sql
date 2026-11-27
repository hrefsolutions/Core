ALTER TABLE notifications ADD COLUMN application_id UUID NULL;

UPDATE notifications AS n
SET application_id = a.id
FROM applications AS a
WHERE a.app_key = n.app_key;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM notifications WHERE application_id IS NULL) THEN
        RAISE EXCEPTION 'Some notifications have an app_key that matches no application; set their application_id before running this migration again.';
    END IF;
END $$;

ALTER TABLE notifications ALTER COLUMN application_id SET NOT NULL;

CREATE INDEX ix_notifications_application_id_created_at_utc ON notifications (application_id, created_at_utc DESC);

ALTER TABLE notifications DROP CONSTRAINT ck_notifications_status;

ALTER TABLE notifications ADD CONSTRAINT ck_notifications_status
    CHECK (status IN ('Pending', 'Processing', 'Sent', 'Failed', 'Cancelled'));

ALTER TABLE audit_logs ADD COLUMN application_id UUID NULL;

ALTER TABLE audit_logs DISABLE TRIGGER audit_logs_block_mutation;

UPDATE audit_logs AS l
SET application_id = a.id
FROM applications AS a
WHERE a.app_key = l.app_key;

ALTER TABLE audit_logs ENABLE TRIGGER audit_logs_block_mutation;

CREATE INDEX ix_audit_logs_application_id_occurred_at_utc_id ON audit_logs (application_id, occurred_at_utc DESC, id DESC);
