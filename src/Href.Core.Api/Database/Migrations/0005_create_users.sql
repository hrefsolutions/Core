CREATE TABLE users (
    id UUID PRIMARY KEY,
    email VARCHAR(254) NOT NULL,
    full_name VARCHAR(150) NOT NULL,
    password_hash VARCHAR(500) NOT NULL,
    email_verified_at_utc TIMESTAMPTZ NULL,
    status VARCHAR(20) NOT NULL,
    locked_at_utc TIMESTAMPTZ NULL,
    lock_reason VARCHAR(500) NULL,
    security_stamp UUID NOT NULL,
    last_login_at_utc TIMESTAMPTZ NULL,
    created_at_utc TIMESTAMPTZ NOT NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_users_status CHECK (status IN ('Active', 'Locked')),
    CONSTRAINT ck_users_lock_state CHECK ((status = 'Locked') = (locked_at_utc IS NOT NULL))
);

CREATE UNIQUE INDEX ux_users_email ON users (email);
