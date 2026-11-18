CREATE TABLE applications (
    id UUID PRIMARY KEY,
    app_key VARCHAR(50) NOT NULL,
    name VARCHAR(100) NOT NULL,
    description VARCHAR(500) NULL,
    status VARCHAR(20) NOT NULL,
    client_secret_hash VARCHAR(64) NOT NULL,
    client_secret_rotated_at_utc TIMESTAMPTZ NOT NULL,
    allow_self_registration BOOLEAN NOT NULL DEFAULT FALSE,
    created_at_utc TIMESTAMPTZ NOT NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_applications_status CHECK (status IN ('Active', 'Suspended'))
);

CREATE UNIQUE INDEX ux_applications_app_key ON applications (app_key);
