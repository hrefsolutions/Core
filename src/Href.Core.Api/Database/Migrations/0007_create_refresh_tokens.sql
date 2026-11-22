CREATE TABLE refresh_tokens (
    id UUID PRIMARY KEY,
    version INTEGER NOT NULL DEFAULT 1,
    user_id UUID NOT NULL,
    application_id UUID NOT NULL,
    family_id UUID NOT NULL,
    token_hash VARCHAR(64) NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL,
    expires_at_utc TIMESTAMPTZ NOT NULL,
    revoked_at_utc TIMESTAMPTZ NULL,
    revoked_reason VARCHAR(30) NULL,
    replaced_by_token_id UUID NULL,
    created_by_ip VARCHAR(45) NULL,
    user_agent VARCHAR(512) NULL,
    CONSTRAINT fk_refresh_tokens_user_id FOREIGN KEY (user_id) REFERENCES users (id),
    CONSTRAINT fk_refresh_tokens_application_id FOREIGN KEY (application_id) REFERENCES applications (id),
    CONSTRAINT fk_refresh_tokens_replaced_by_token_id FOREIGN KEY (replaced_by_token_id) REFERENCES refresh_tokens (id),
    CONSTRAINT ck_refresh_tokens_token_hash CHECK (char_length(token_hash) = 64),
    CONSTRAINT ck_refresh_tokens_expiry CHECK (expires_at_utc > created_at_utc),
    CONSTRAINT ck_refresh_tokens_revoked_reason CHECK (
        revoked_reason IS NULL
        OR revoked_reason IN ('Rotated', 'Logout', 'ReuseDetected', 'PasswordReset', 'UserLocked', 'AdminRevoked')),
    CONSTRAINT ck_refresh_tokens_revocation CHECK ((revoked_at_utc IS NULL) = (revoked_reason IS NULL)),
    CONSTRAINT ck_refresh_tokens_replaced_by CHECK (replaced_by_token_id IS NULL OR revoked_reason = 'Rotated')
);

CREATE UNIQUE INDEX ux_refresh_tokens_token_hash ON refresh_tokens (token_hash);

CREATE INDEX ix_refresh_tokens_user_id ON refresh_tokens (user_id);

CREATE INDEX ix_refresh_tokens_family_id ON refresh_tokens (family_id);

CREATE INDEX ix_refresh_tokens_application_id ON refresh_tokens (application_id);
