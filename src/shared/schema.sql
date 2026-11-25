PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;
PRAGMA busy_timeout = 5000;

CREATE TABLE IF NOT EXISTS users (
    id TEXT PRIMARY KEY,
    google_id      TEXT UNIQUE NOT NULL,
    email          TEXT UNIQUE NOT NULL COLLATE NOCASE,
    full_name      TEXT NOT NULL,
    avatar_url     TEXT NULL,
    is_super_admin INTEGER NOT NULL DEFAULT 0 CHECK (is_super_admin IN (0, 1)),
    created_at     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_users_google_id ON users(google_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_users_email ON users(email);

CREATE TABLE IF NOT EXISTS organizations (
    id         TEXT PRIMARY KEY,
    name       TEXT NOT NULL,
    slug       TEXT UNIQUE NOT NULL COLLATE NOCASE,
    is_active  INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_organizations_slug ON organizations(slug);

CREATE TABLE IF NOT EXISTS departments (
    id              TEXT PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    name            TEXT NOT NULL,
    code            TEXT NULL,
    created_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_departments_org_name ON departments(organization_id, name);

CREATE TABLE IF NOT EXISTS custody_holders (
    id                  TEXT PRIMARY KEY,
    organization_id     TEXT NOT NULL REFERENCES organizations(id) ON DELETE RESTRICT,
    department_id       TEXT NULL REFERENCES departments(id) ON DELETE SET NULL,
    user_id             TEXT NULL REFERENCES users(id) ON DELETE SET NULL,
    holder_type         INTEGER NOT NULL DEFAULT 1 CHECK (holder_type IN (0, 1)),
    name                TEXT NOT NULL,
    internal_identifier TEXT NULL,
    invited_email       TEXT NULL COLLATE NOCASE,
    role                INTEGER NOT NULL DEFAULT 0 CHECK (role IN (0, 1, 2)),
    attendance_status   INTEGER NOT NULL DEFAULT 0 CHECK (attendance_status IN (0, 1, 2)),
    phone_number        TEXT NULL,
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    created_at          TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at          TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS idx_custody_holders_org ON custody_holders(organization_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_custody_holders_org_internal
    ON custody_holders(organization_id, internal_identifier) WHERE internal_identifier IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS idx_custody_holders_org_user
    ON custody_holders(organization_id, user_id) WHERE user_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_custody_holders_org_email ON custody_holders(organization_id, invited_email);
CREATE INDEX IF NOT EXISTS idx_custody_holders_department ON custody_holders(department_id);

CREATE TABLE IF NOT EXISTS certifications (
    id              TEXT PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    name            TEXT NOT NULL,
    description     TEXT NULL,
    validity_months INTEGER NULL CHECK (validity_months IS NULL OR validity_months > 0),
    created_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS idx_certifications_org ON certifications(organization_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_certifications_org_name ON certifications(organization_id, name);

CREATE TABLE IF NOT EXISTS holder_certifications (
    id                 TEXT PRIMARY KEY,
    holder_id          TEXT NOT NULL REFERENCES custody_holders(id) ON DELETE RESTRICT,
    certification_id   TEXT NOT NULL REFERENCES certifications(id) ON DELETE RESTRICT,
    acquired_at        TEXT NOT NULL,
    expires_at         TEXT NULL,
    certificate_number TEXT NULL,
    notes              TEXT NULL,
    created_at         TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at         TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    UNIQUE (holder_id, certification_id),
    CHECK (expires_at IS NULL OR expires_at >= acquired_at)
);
CREATE INDEX IF NOT EXISTS idx_holder_cert_holder ON holder_certifications(holder_id);
CREATE INDEX IF NOT EXISTS idx_holder_cert_cert ON holder_certifications(certification_id);

CREATE TABLE IF NOT EXISTS item_categories (
    id              TEXT PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    name            TEXT NOT NULL,
    description     TEXT NULL,
    created_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS idx_item_categories_org ON item_categories(organization_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_item_categories_org_name ON item_categories(organization_id, name);

CREATE TABLE IF NOT EXISTS item_types (
    id              TEXT PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    category_id     TEXT NULL REFERENCES item_categories(id) ON DELETE SET NULL,
    name            TEXT NOT NULL,
    tracking_type   INTEGER NOT NULL DEFAULT 1 CHECK (tracking_type IN (0, 1)),
    unit_of_measure TEXT NOT NULL DEFAULT 'unit',
    description     TEXT NULL,
    created_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    UNIQUE (organization_id, name)
);
CREATE INDEX IF NOT EXISTS idx_item_types_org ON item_types(organization_id);
CREATE INDEX IF NOT EXISTS idx_item_types_category ON item_types(category_id);

CREATE TABLE IF NOT EXISTS serialized_items (
    id                   TEXT PRIMARY KEY,
    organization_id      TEXT NOT NULL REFERENCES organizations(id) ON DELETE RESTRICT,
    item_type_id         TEXT NOT NULL REFERENCES item_types(id) ON DELETE RESTRICT,
    serial_number        TEXT NULL,
    barcode              TEXT NOT NULL,
    current_holder_id    TEXT NULL REFERENCES custody_holders(id) ON DELETE RESTRICT,
    location_holder_id   TEXT NULL REFERENCES custody_holders(id) ON DELETE RESTRICT,
    location_custom_text TEXT NULL,
    status               INTEGER NOT NULL DEFAULT 0 CHECK (status IN (0, 1, 2, 3)),
    created_at           TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at           TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS idx_serialized_items_org ON serialized_items(organization_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_serialized_org_barcode ON serialized_items(organization_id, barcode);
CREATE UNIQUE INDEX IF NOT EXISTS idx_serialized_org_serial
    ON serialized_items(organization_id, serial_number) WHERE serial_number IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_serialized_current_holder ON serialized_items(current_holder_id);
CREATE INDEX IF NOT EXISTS idx_serialized_location_holder ON serialized_items(location_holder_id);
CREATE INDEX IF NOT EXISTS idx_serialized_item_type ON serialized_items(item_type_id);

CREATE TABLE IF NOT EXISTS inventory_stocks (
    id              TEXT PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations(id) ON DELETE RESTRICT,
    item_type_id    TEXT NOT NULL REFERENCES item_types(id) ON DELETE RESTRICT,
    holder_id       TEXT NOT NULL REFERENCES custody_holders(id) ON DELETE RESTRICT,
    quantity        INTEGER NOT NULL DEFAULT 0,
    created_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    CHECK (quantity >= 0)
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_inventory_stocks_holder_item ON inventory_stocks(holder_id, item_type_id);
CREATE INDEX IF NOT EXISTS idx_inventory_stocks_org ON inventory_stocks(organization_id);

CREATE TABLE IF NOT EXISTS transfer_templates (
    id                       TEXT PRIMARY KEY,
    organization_id          TEXT NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    name                     TEXT NOT NULL,
    description              TEXT NULL,
    default_source_holder_id TEXT NULL REFERENCES custody_holders(id) ON DELETE RESTRICT,
    default_dest_holder_id   TEXT NULL REFERENCES custody_holders(id) ON DELETE RESTRICT,
    is_active                INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    created_at               TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at               TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_transfer_templates_org_name ON transfer_templates(organization_id, name);

CREATE TABLE IF NOT EXISTS transfer_template_lines (
    template_id  TEXT NOT NULL REFERENCES transfer_templates(id) ON DELETE CASCADE,
    line_number  INTEGER NOT NULL,
    item_type_id TEXT NOT NULL REFERENCES item_types(id) ON DELETE RESTRICT,
    quantity     INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY (template_id, line_number),
    UNIQUE (template_id, item_type_id),
    CHECK (quantity > 0)
);
CREATE INDEX IF NOT EXISTS idx_template_lines_item_type ON transfer_template_lines(item_type_id);

CREATE TABLE IF NOT EXISTS custody_transfers (
    id                   TEXT PRIMARY KEY,
    organization_id      TEXT NOT NULL REFERENCES organizations(id) ON DELETE RESTRICT,
    transfer_template_id TEXT NULL REFERENCES transfer_templates(id) ON DELETE SET NULL,
    transfer_number      TEXT NOT NULL,
    admin_holder_id      TEXT NOT NULL REFERENCES custody_holders(id) ON DELETE RESTRICT,
    source_holder_id     TEXT NULL REFERENCES custody_holders(id) ON DELETE RESTRICT,
    dest_holder_id       TEXT NULL REFERENCES custody_holders(id) ON DELETE RESTRICT,
    notes                TEXT NULL,
    transferred_at       TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    CHECK (
        (source_holder_id IS NOT NULL AND dest_holder_id IS NOT NULL)
        OR (notes IS NOT NULL AND TRIM(notes) != '')
    ),
    CHECK (
        source_holder_id IS NULL
        OR dest_holder_id IS NULL
        OR source_holder_id <> dest_holder_id
    )
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_transfers_org_number ON custody_transfers(organization_id, transfer_number);
CREATE INDEX IF NOT EXISTS idx_transfers_source ON custody_transfers(source_holder_id);
CREATE INDEX IF NOT EXISTS idx_transfers_dest ON custody_transfers(dest_holder_id);
CREATE INDEX IF NOT EXISTS idx_transfers_org_date ON custody_transfers(organization_id, transferred_at DESC);

CREATE TABLE IF NOT EXISTS custody_transfer_lines (
    transfer_id        TEXT NOT NULL REFERENCES custody_transfers(id) ON DELETE CASCADE,
    line_number        INTEGER NOT NULL,
    serialized_item_id TEXT NULL REFERENCES serialized_items(id) ON DELETE RESTRICT,
    item_type_id       TEXT NULL REFERENCES item_types(id) ON DELETE RESTRICT,
    quantity           INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY (transfer_id, line_number),
    CHECK (
        (serialized_item_id IS NOT NULL AND item_type_id IS NULL AND quantity = 1)
        OR (serialized_item_id IS NULL AND item_type_id IS NOT NULL AND quantity > 0)
    )
);
CREATE INDEX IF NOT EXISTS idx_transfer_lines_serialized_item ON custody_transfer_lines(serialized_item_id);

CREATE TABLE IF NOT EXISTS serialized_item_location_logs (
    id                        TEXT PRIMARY KEY,
    organization_id           TEXT NOT NULL REFERENCES organizations(id) ON DELETE RESTRICT,
    serialized_item_id        TEXT NOT NULL REFERENCES serialized_items(id) ON DELETE RESTRICT,
    admin_holder_id           TEXT NOT NULL REFERENCES custody_holders(id) ON DELETE RESTRICT,
    from_location_holder_id   TEXT NULL REFERENCES custody_holders(id) ON DELETE RESTRICT,
    from_location_custom_text TEXT NULL,
    to_location_holder_id     TEXT NULL REFERENCES custody_holders(id) ON DELETE RESTRICT,
    to_location_custom_text   TEXT NULL,
    notes                     TEXT NULL,
    recorded_at               TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    CHECK (
        to_location_holder_id IS NOT NULL
        OR (to_location_custom_text IS NOT NULL AND TRIM(to_location_custom_text) <> '')
    )
);
CREATE INDEX IF NOT EXISTS idx_location_logs_item ON serialized_item_location_logs(serialized_item_id);
CREATE INDEX IF NOT EXISTS idx_location_logs_org_date ON serialized_item_location_logs(organization_id, recorded_at);

CREATE TABLE IF NOT EXISTS audit_sessions (
    id                     TEXT PRIMARY KEY,
    organization_id        TEXT NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    title                  TEXT NOT NULL,
    initiated_by_holder_id TEXT NOT NULL REFERENCES custody_holders(id) ON DELETE RESTRICT,
    status                 INTEGER NOT NULL DEFAULT 0 CHECK (status IN (0, 1, 2)),
    opened_at              TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    closed_at              TEXT NULL,
    updated_at             TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    CHECK (closed_at IS NULL OR closed_at >= opened_at)
);
CREATE INDEX IF NOT EXISTS idx_audit_sessions_org ON audit_sessions(organization_id);

CREATE TABLE IF NOT EXISTS audit_item_records (
    id                   TEXT PRIMARY KEY,
    audit_session_id     TEXT NOT NULL REFERENCES audit_sessions(id) ON DELETE CASCADE,
    serialized_item_id   TEXT NOT NULL REFERENCES serialized_items(id) ON DELETE RESTRICT,
    expected_holder_id   TEXT NULL REFERENCES custody_holders(id) ON DELETE RESTRICT,
    scan_status          INTEGER NOT NULL DEFAULT 0 CHECK (scan_status IN (0, 1, 2, 3)),
    scanned_by_holder_id TEXT NULL REFERENCES custody_holders(id) ON DELETE RESTRICT,
    scanned_at           TEXT NULL,
    resolved_by_admin_id TEXT NULL REFERENCES custody_holders(id) ON DELETE RESTRICT,
    resolution_notes     TEXT NULL,
    created_at           TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at           TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_audit_item_session_item ON audit_item_records(audit_session_id, serialized_item_id);
CREATE INDEX IF NOT EXISTS idx_audit_item_session_status ON audit_item_records(audit_session_id, scan_status);
CREATE INDEX IF NOT EXISTS idx_audit_item_holder ON audit_item_records(scanned_by_holder_id);

CREATE TABLE IF NOT EXISTS user_sessions (
    token_hash    TEXT PRIMARY KEY,
    user_id       TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    active_org_id TEXT NULL REFERENCES organizations(id) ON DELETE SET NULL,
    expires_at    TEXT NOT NULL,
    created_at    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS idx_user_sessions_user ON user_sessions(user_id);
CREATE INDEX IF NOT EXISTS idx_user_sessions_expires ON user_sessions(expires_at);

CREATE TABLE IF NOT EXISTS idempotency_keys (
    organization_id TEXT NOT NULL REFERENCES organizations(id) ON DELETE RESTRICT,
    idempotency_key TEXT NOT NULL,
    endpoint_name   TEXT NOT NULL,
    response_json   TEXT NOT NULL,
    created_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    PRIMARY KEY (organization_id, idempotency_key, endpoint_name)
);

-- ============================================================================
-- Automatic updated_at triggers for mutable tables
-- (Append-only/immutable tables are intentionally excluded:
--  custody_transfers, custody_transfer_lines, serialized_item_location_logs,
--  user_sessions, idempotency_keys, transfer_template_lines)
-- ============================================================================

CREATE TRIGGER IF NOT EXISTS trg_users_updated_at
AFTER UPDATE ON users
WHEN NEW.updated_at = OLD.updated_at
BEGIN
    UPDATE users
    SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now')
    WHERE id = OLD.id;
END;

CREATE TRIGGER IF NOT EXISTS trg_organizations_updated_at
AFTER UPDATE ON organizations
WHEN NEW.updated_at = OLD.updated_at
BEGIN
    UPDATE organizations
    SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now')
    WHERE id = OLD.id;
END;

CREATE TRIGGER IF NOT EXISTS trg_departments_updated_at
AFTER UPDATE ON departments
WHEN NEW.updated_at = OLD.updated_at
BEGIN
    UPDATE departments
    SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now')
    WHERE id = OLD.id;
END;

CREATE TRIGGER IF NOT EXISTS trg_custody_holders_updated_at
AFTER UPDATE ON custody_holders
WHEN NEW.updated_at = OLD.updated_at
BEGIN
    UPDATE custody_holders
    SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now')
    WHERE id = OLD.id;
END;

CREATE TRIGGER IF NOT EXISTS trg_certifications_updated_at
AFTER UPDATE ON certifications
WHEN NEW.updated_at = OLD.updated_at
BEGIN
    UPDATE certifications
    SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now')
    WHERE id = OLD.id;
END;

CREATE TRIGGER IF NOT EXISTS trg_holder_certifications_updated_at
AFTER UPDATE ON holder_certifications
WHEN NEW.updated_at = OLD.updated_at
BEGIN
    UPDATE holder_certifications
    SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now')
    WHERE id = OLD.id;
END;

CREATE TRIGGER IF NOT EXISTS trg_item_categories_updated_at
AFTER UPDATE ON item_categories
WHEN NEW.updated_at = OLD.updated_at
BEGIN
    UPDATE item_categories
    SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now')
    WHERE id = OLD.id;
END;

CREATE TRIGGER IF NOT EXISTS trg_item_types_updated_at
AFTER UPDATE ON item_types
WHEN NEW.updated_at = OLD.updated_at
BEGIN
    UPDATE item_types
    SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now')
    WHERE id = OLD.id;
END;

CREATE TRIGGER IF NOT EXISTS trg_serialized_items_updated_at
AFTER UPDATE ON serialized_items
WHEN NEW.updated_at = OLD.updated_at
BEGIN
    UPDATE serialized_items
    SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now')
    WHERE id = OLD.id;
END;

CREATE TRIGGER IF NOT EXISTS trg_inventory_stocks_updated_at
AFTER UPDATE ON inventory_stocks
WHEN NEW.updated_at = OLD.updated_at
BEGIN
    UPDATE inventory_stocks
    SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now')
    WHERE id = OLD.id;
END;

CREATE TRIGGER IF NOT EXISTS trg_transfer_templates_updated_at
AFTER UPDATE ON transfer_templates
WHEN NEW.updated_at = OLD.updated_at
BEGIN
    UPDATE transfer_templates
    SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now')
    WHERE id = OLD.id;
END;

CREATE TRIGGER IF NOT EXISTS trg_audit_sessions_updated_at
AFTER UPDATE ON audit_sessions
WHEN NEW.updated_at = OLD.updated_at
BEGIN
    UPDATE audit_sessions
    SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now')
    WHERE id = OLD.id;
END;

CREATE TRIGGER IF NOT EXISTS trg_audit_item_records_updated_at
AFTER UPDATE ON audit_item_records
WHEN NEW.updated_at = OLD.updated_at
BEGIN
    UPDATE audit_item_records
    SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now')
    WHERE id = OLD.id;
END;
