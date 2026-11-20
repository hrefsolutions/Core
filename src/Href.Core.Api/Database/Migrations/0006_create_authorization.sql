CREATE TABLE permissions (
    id UUID PRIMARY KEY,
    application_id UUID NOT NULL,
    key VARCHAR(100) NOT NULL,
    description VARCHAR(500) NULL,
    created_at_utc TIMESTAMPTZ NOT NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL,
    CONSTRAINT fk_permissions_application_id FOREIGN KEY (application_id) REFERENCES applications (id)
);

CREATE UNIQUE INDEX ux_permissions_application_id_key ON permissions (application_id, key);

CREATE TABLE roles (
    id UUID PRIMARY KEY,
    application_id UUID NOT NULL,
    name VARCHAR(100) NOT NULL,
    description VARCHAR(500) NULL,
    is_default BOOLEAN NOT NULL DEFAULT FALSE,
    created_at_utc TIMESTAMPTZ NOT NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL,
    CONSTRAINT fk_roles_application_id FOREIGN KEY (application_id) REFERENCES applications (id)
);

CREATE UNIQUE INDEX ux_roles_application_id_name ON roles (application_id, lower(name));
CREATE UNIQUE INDEX ux_roles_application_id_default ON roles (application_id) WHERE is_default;

CREATE TABLE role_permissions (
    role_id UUID NOT NULL,
    permission_id UUID NOT NULL,
    CONSTRAINT pk_role_permissions PRIMARY KEY (role_id, permission_id),
    CONSTRAINT fk_role_permissions_role_id FOREIGN KEY (role_id) REFERENCES roles (id) ON DELETE CASCADE,
    CONSTRAINT fk_role_permissions_permission_id FOREIGN KEY (permission_id) REFERENCES permissions (id) ON DELETE CASCADE
);

CREATE INDEX ix_role_permissions_permission_id ON role_permissions (permission_id);

CREATE TABLE user_application_accesses (
    id UUID PRIMARY KEY,
    user_id UUID NOT NULL,
    application_id UUID NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL,
    CONSTRAINT fk_user_application_accesses_user_id FOREIGN KEY (user_id) REFERENCES users (id),
    CONSTRAINT fk_user_application_accesses_application_id FOREIGN KEY (application_id) REFERENCES applications (id)
);

CREATE UNIQUE INDEX ux_user_application_accesses_user_id_application_id ON user_application_accesses (user_id, application_id);
CREATE INDEX ix_user_application_accesses_application_id ON user_application_accesses (application_id);

CREATE TABLE user_application_access_roles (
    user_application_access_id UUID NOT NULL,
    role_id UUID NOT NULL,
    CONSTRAINT pk_user_application_access_roles PRIMARY KEY (user_application_access_id, role_id),
    CONSTRAINT fk_user_application_access_roles_access_id FOREIGN KEY (user_application_access_id) REFERENCES user_application_accesses (id) ON DELETE CASCADE,
    CONSTRAINT fk_user_application_access_roles_role_id FOREIGN KEY (role_id) REFERENCES roles (id) ON DELETE CASCADE
);

CREATE INDEX ix_user_application_access_roles_role_id ON user_application_access_roles (role_id);
