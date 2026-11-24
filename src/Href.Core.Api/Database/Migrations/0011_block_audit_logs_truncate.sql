CREATE TRIGGER audit_logs_block_truncate
BEFORE TRUNCATE ON audit_logs
FOR EACH STATEMENT
EXECUTE FUNCTION prevent_audit_logs_mutation();
