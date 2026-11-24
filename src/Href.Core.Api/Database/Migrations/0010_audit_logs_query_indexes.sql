CREATE INDEX ix_audit_logs_occurred_at_utc_id ON audit_logs (occurred_at_utc DESC, id DESC);
CREATE INDEX ix_audit_logs_app_key_occurred_at_utc_id ON audit_logs (app_key, occurred_at_utc DESC, id DESC);
CREATE INDEX ix_audit_logs_actor_occurred_at_utc_id ON audit_logs (actor_type, actor_id, occurred_at_utc DESC, id DESC);

DROP INDEX ix_audit_logs_occurred_at_utc;
DROP INDEX ix_audit_logs_app_key_occurred_at_utc;
