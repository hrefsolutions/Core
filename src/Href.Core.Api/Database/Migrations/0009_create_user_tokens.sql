CREATE TABLE user_tokens (
    id UUID PRIMARY KEY,
    user_id UUID NOT NULL,
    application_id UUID NOT NULL,
    purpose VARCHAR(30) NOT NULL,
    token_hash VARCHAR(64) NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL,
    expires_at_utc TIMESTAMPTZ NOT NULL,
    consumed_at_utc TIMESTAMPTZ NULL,
    revoked_at_utc TIMESTAMPTZ NULL,
    CONSTRAINT fk_user_tokens_user_id FOREIGN KEY (user_id) REFERENCES users (id),
    CONSTRAINT fk_user_tokens_application_id FOREIGN KEY (application_id) REFERENCES applications (id),
    CONSTRAINT ck_user_tokens_purpose CHECK (purpose IN ('EmailVerification', 'PasswordReset')),
    CONSTRAINT ck_user_tokens_token_hash CHECK (char_length(token_hash) = 64),
    CONSTRAINT ck_user_tokens_expiry CHECK (expires_at_utc > created_at_utc),
    CONSTRAINT ck_user_tokens_single_end CHECK (consumed_at_utc IS NULL OR revoked_at_utc IS NULL)
);

CREATE UNIQUE INDEX ux_user_tokens_token_hash ON user_tokens (token_hash);

CREATE INDEX ix_user_tokens_user_id_purpose_open ON user_tokens (user_id, purpose)
    WHERE consumed_at_utc IS NULL AND revoked_at_utc IS NULL;
