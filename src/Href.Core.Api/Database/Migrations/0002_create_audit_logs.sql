CREATE TABLE audit_logs (
    id UUID PRIMARY KEY,
    occurred_at_utc TIMESTAMPTZ NOT NULL,
    action VARCHAR(100) NOT NULL,
    entity_type VARCHAR(100) NULL,
    entity_id VARCHAR(100) NULL,
    actor_type VARCHAR(20) NOT NULL,
    actor_id VARCHAR(100) NULL,
    app_key VARCHAR(50) NULL,
    old_values JSONB NULL,
    new_values JSONB NULL,
    correlation_id VARCHAR(64) NULL,
    ip_address VARCHAR(45) NULL,
    user_agent VARCHAR(500) NULL,
    CONSTRAINT ck_audit_logs_actor_type CHECK (actor_type IN ('User', 'Application', 'System', 'Anonymous'))
);

CREATE INDEX ix_audit_logs_occurred_at_utc ON audit_logs (occurred_at_utc DESC);
CREATE INDEX ix_audit_logs_app_key_occurred_at_utc ON audit_logs (app_key, occurred_at_utc DESC);
CREATE INDEX ix_audit_logs_correlation_id ON audit_logs (correlation_id);
CREATE INDEX ix_audit_logs_entity ON audit_logs (entity_type, entity_id);

CREATE OR REPLACE FUNCTION prevent_audit_logs_mutation()
RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'audit_logs is append-only and cannot be modified or deleted.';
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER audit_logs_block_mutation
BEFORE UPDATE OR DELETE ON audit_logs
FOR EACH ROW
EXECUTE FUNCTION prevent_audit_logs_mutation();
