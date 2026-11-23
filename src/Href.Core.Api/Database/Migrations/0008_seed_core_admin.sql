DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM applications
        WHERE app_key = 'core' AND id <> '01937a00-0000-7000-8000-000000000001') THEN
        RAISE EXCEPTION 'An application with app_key core already exists with another id; the reserved core application must use id 01937a00-0000-7000-8000-000000000001.';
    END IF;
END $$;

INSERT INTO applications (
    id, app_key, name, description, status, client_secret_hash, client_secret_rotated_at_utc,
    allow_self_registration, created_at_utc, updated_at_utc)
VALUES (
    '01937a00-0000-7000-8000-000000000001',
    'core',
    'Href Core Admin',
    'Reserved application of the admin frontend backend. Its secret is set by Bootstrap__CoreClientSecret or rotated by an admin.',
    'Active',
    encode(sha256(convert_to(gen_random_uuid()::text || gen_random_uuid()::text, 'UTF8')), 'hex'),
    now(),
    FALSE,
    now(),
    now())
ON CONFLICT (id) DO NOTHING;

INSERT INTO permissions (id, application_id, key, description, created_at_utc, updated_at_utc)
VALUES (
    '01937a00-0000-7000-8000-000000000002',
    '01937a00-0000-7000-8000-000000000001',
    'core.admin',
    'Full access to the Core admin endpoints.',
    now(),
    now())
ON CONFLICT (id) DO NOTHING;

INSERT INTO roles (id, application_id, name, description, is_default, created_at_utc, updated_at_utc)
VALUES (
    '01937a00-0000-7000-8000-000000000003',
    '01937a00-0000-7000-8000-000000000001',
    'admin',
    'Core administrators.',
    FALSE,
    now(),
    now())
ON CONFLICT (id) DO NOTHING;

INSERT INTO role_permissions (role_id, permission_id)
VALUES ('01937a00-0000-7000-8000-000000000003', '01937a00-0000-7000-8000-000000000002')
ON CONFLICT (role_id, permission_id) DO NOTHING;
