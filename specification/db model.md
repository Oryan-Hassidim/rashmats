# Data Model & Schema - Rashmatz

A unified, efficient, and modern relational data architecture. SQLite-first and 100% compatible with **PostgreSQL** without requiring substantial schema changes.

## 1. Cross-DB Compatibility & Optimization Principles

1. **Identifiers (Primary Keys):** Single-column UUID primary keys (`id TEXT PRIMARY KEY` using time-sorted UUIDv7 stored as `TEXT` in SQLite, `UUID` in PostgreSQL). These identifiers prevent data exposure and allow offline ID generation on the client side. Composite primary keys are reserved strictly for ordered weak entities (`transfer_template_lines`, `custody_transfer_lines`) and `idempotency_keys`.
2. **Strings & Case-Insensitive Uniqueness:** Use of standard `TEXT` type (in PostgreSQL and SQLite there is no advantage to `VARCHAR(n)`). Emails and slugs (`users.email`, `organizations.slug`, `custody_holders.invited_email`) use `COLLATE NOCASE` in SQLite (or `CITEXT` / lowercased indexes in PostgreSQL) for case-insensitive matching and uniqueness.
3. **Compact Statuses, Enums & Booleans:** Managed as small integers (`SMALLINT` in Postgres / `INTEGER` in SQLite) occupying only 1–2 bytes, enforced at the database layer with strict `CHECK` constraints (`IN (0, 1)`, `IN (0, 1, 2)`, `IN (0, 1, 2, 3)`) for all enum and boolean-like columns (`is_active`, `is_super_admin`).
4. **Explicit Referential Integrity (`ON DELETE` Behavior):** Never rely on SQLite defaults. Every foreign key declares an explicit `ON DELETE` action:
   * `ON DELETE CASCADE` for true child entities (`departments`, `certifications`, `item_categories`, `item_types`, `transfer_templates`, `audit_sessions` -> `organizations`) and weak/session entities (`transfer_template_lines`, `custody_transfer_lines`, `audit_item_records`, `user_sessions.user_id`).
   * `ON DELETE SET NULL` for optional references (`department_id`, `user_id`, `category_id`, `transfer_template_id`, `active_org_id`).
   * `ON DELETE RESTRICT` for audit-critical and custody references (`custody_holders`, `serialized_items`, `custody_transfers`, `serialized_item_location_logs`, etc.).
5. **Unified Custody Holder Entity (`custody_holders`):** Eliminating the separation between a person and a location. Every actor that can hold equipment (employee, contractor, warehouse, vehicle, shelf) is represented in a single entity.
6. **Distinction Between Custody and Physical Location (Flexible OR Principle):**
   * **Custody:** Defines legal/managerial accountability for the item (`current_holder_id`).
   * **Physical Location:** Can be defined as a registered entity in the system (`location_holder_id`), as free text (`location_custom_text`), or **as both together** (e.g., entity "Main Warehouse" + text detail "Cabinet 3, Drawer B").
   * Every location update without a transfer of ownership is recorded in a dedicated immutable log (`serialized_item_location_logs`) validated to ensure a non-empty destination.
7. **Predefined Transfer Templates (Transfer Templates / Kits):** Allow defining "equipment kits" or preset recurring transfers (e.g., employee onboarding kit, field deployment kit), with optional source/destination and a deduplicated list of products and quantities (`UNIQUE (template_id, item_type_id)`). When executing an actual transfer from a template, the specific serialized items are selected or scanned.
8. **ISO8601 Timestamps & `updated_at` Automation vs. Append-Only:**
   * All timestamp defaults use ISO8601 UTC format (`DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` in SQLite rather than `CURRENT_TIMESTAMP`).
   * All 13 mutable tables include both `created_at` and `updated_at` columns along with automatic `AFTER UPDATE` triggers (`WHEN NEW.updated_at = OLD.updated_at`) to maintain `updated_at`.
   * Immutable / append-only tables (`custody_transfers`, `custody_transfer_lines`, `serialized_item_location_logs`, `user_sessions`, `idempotency_keys`, `transfer_template_lines`) intentionally omit `updated_at` and have **no** `updated_at` triggers.
9. **Lean Indexing:** Removal of redundant indexes on low-cardinality columns (such as single status columns). Retaining indexes only for tenant scoping (`organization_id` on major tenant tables), active FKs in JOINs, session expiration cleanup (`user_sessions(expires_at)`), and business `UNIQUE` constraints.
10. **Architectural Simplicity Guardrails (Do Not Add):** Keep the schema simple—do **not** introduce composite primary keys everywhere, composite tenant-aware foreign keys on every relationship, `organization_id` inside every foreign key, event sourcing, or a generic soft-delete framework (use `is_active` flags where appropriate).

---

## 2. Unified High-Level ERD

```
User (Global Google Auth)
  ├── UserSession (Auth session & active organization)
  └── Organization (Tenant)
        ├── Department (Organizational units / Teams)
        ├── CustodyHolder (Person / Warehouse / Vehicle / Shelf)
        │     ├── HolderCertification (Skills & Permits)
        │     ├── SerializedItem (Items currently held / located)
        │     └── InventoryStock (Bulk stock balances)
        ├── Certification (Catalog of skills/trainings)
        ├── ItemCategory (Catalog categories)
        ├── ItemType (Catalog of equipment definitions)
        ├── TransferTemplate (Predefined kits/transfer presets)
        │     └── TransferTemplateLine (Item type & quantity)
        ├── CustodyTransfer (Transfer header: Inbound / Internal / Outbound)
        │     └── CustodyTransferLine (Specific items & quantities transferred)
        ├── SerializedItemLocationLog (Append-only physical location movement log)
        ├── AuditSession
        │     └── AuditItemRecord (Pre-populated checklist & scan execution)
        └── IdempotencyKey (API mutation deduplication cache)
```

---

## 3. Identity & Tenancy Management

### `users` (Global Google User Accounts)
Users authenticated via Google OAuth. They do not belong directly to a single organization. Includes automatic `updated_at` trigger (`trg_users_updated_at`).

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Global unique identifier |
| `google_id` | `TEXT` | `UNIQUE, NOT NULL` | Permanent Google user ID (`sub`) |
| `email` | `TEXT` | `UNIQUE, NOT NULL, COLLATE NOCASE` | Email address (case-insensitive) |
| `full_name` | `TEXT` | `NOT NULL` | Full name as configured in Google account |
| `avatar_url` | `TEXT` | `NULLABLE` | Google profile picture |
| `is_super_admin` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 0, CHECK (is_super_admin IN (0, 1))` | `1` = Global super admin, `0` = Standard user |
| `created_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Initial creation timestamp (ISO8601 UTC) |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Last update timestamp (ISO8601 UTC) |

*Indexes:*
* `CREATE UNIQUE INDEX IF NOT EXISTS idx_users_google_id ON users(google_id);`
* `CREATE UNIQUE INDEX IF NOT EXISTS idx_users_email ON users(email);`

### `organizations` (Tenants)
Includes automatic `updated_at` trigger (`trg_organizations_updated_at`).

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Organization identifier |
| `name` | `TEXT` | `NOT NULL` | Organization name |
| `slug` | `TEXT` | `UNIQUE, NOT NULL, COLLATE NOCASE` | Textual identifier for routes and access (case-insensitive) |
| `is_active` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 1, CHECK (is_active IN (0, 1))` | `1` = Active, `0` = Disabled |
| `created_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Organization creation timestamp (ISO8601 UTC) |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Organization settings update timestamp (ISO8601 UTC) |

*Indexes:*
* `CREATE UNIQUE INDEX IF NOT EXISTS idx_organizations_slug ON organizations(slug);`

### `departments` (Organization Departments / Divisions / Units)
Internal organizational structure for assigning employees, teams, and dedicated warehouses. Includes automatic `updated_at` trigger (`trg_departments_updated_at`).

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Unique department identifier |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id) ON DELETE CASCADE` | Organization association |
| `name` | `TEXT` | `NOT NULL` | Department / team name |
| `code` | `TEXT` | `NULLABLE` | Short identifier code (e.g., "OPS", "LOG") |
| `created_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Creation timestamp (ISO8601 UTC) |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Details update timestamp (ISO8601 UTC) |

*Indexes:*
* `CREATE UNIQUE INDEX IF NOT EXISTS idx_departments_org_name ON departments(organization_id, name);`

---

## 4. Unified Custody Holder, Attendance & Certifications (Custodians, Attendance & Certifications)

### `custody_holders` (Equipment Holder – Person or Location)
A unified table consolidating people and locations. Also serves as the authorized Whitelist for the organization and for tracking attendance of organization personnel. Includes automatic `updated_at` trigger (`trg_custody_holders_updated_at`).

*Holder Type Mapping (`holder_type`):*
* `0` = Location (warehouse, container, vehicle, shelf)
* `1` = Person / employee / organization member

*Permission Role Mapping (`role`):*
* `0` = No system access (relevant for locations or employees without UI access)
* `1` = MEMBER (End user: view their own equipment, perform audit scans)
* `2` = ADMIN (System administrator: transfers, audits, item and permissions management)

*Attendance Status Mapping (`attendance_status` – relevant for people):*
* `0` = Inactive / Unavailable (`INACTIVE`)
* `1` = Physically present at organization (`PRESENT`)
* `2` = At home / Remote (`HOME / REMOTE`)

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Unique holder identifier |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id) ON DELETE RESTRICT` | Organization association |
| `department_id` | `UUID / TEXT` | `NULLABLE, REFERENCES departments(id) ON DELETE SET NULL` | Department / team association in organization |
| `user_id` | `UUID / TEXT` | `NULLABLE, REFERENCES users(id) ON DELETE SET NULL` | Link to authenticated Google account (if a person) |
| `holder_type` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 1, CHECK (holder_type IN (0, 1))` | `0` = Location, `1` = Person |
| `name` | `TEXT` | `NOT NULL` | Name of the person or location |
| `internal_identifier` | `TEXT` | `NULLABLE` | Internal identifier (employee number, warehouse code) |
| `invited_email` | `TEXT` | `NULLABLE, COLLATE NOCASE` | Whitelist email (for people, case-insensitive) |
| `role` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 0, CHECK (role IN (0, 1, 2))` | Permission level in organization (`0` = None, `1` = Member, `2` = Admin) |
| `attendance_status` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 0, CHECK (attendance_status IN (0, 1, 2))` | Attendance status (`0` = Inactive, `1` = Present, `2` = Home) |
| `phone_number` | `TEXT` | `NULLABLE` | Contact phone number |
| `is_active` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 1, CHECK (is_active IN (0, 1))` | `1` = Active, `0` = Disabled |
| `created_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Addition timestamp (ISO8601 UTC) |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Last update timestamp (ISO8601 UTC) |

*Indexes & Constraints:*
* `CREATE INDEX IF NOT EXISTS idx_custody_holders_org ON custody_holders(organization_id);`
* `CREATE UNIQUE INDEX IF NOT EXISTS idx_custody_holders_org_internal ON custody_holders(organization_id, internal_identifier) WHERE internal_identifier IS NOT NULL;`
* `CREATE UNIQUE INDEX IF NOT EXISTS idx_custody_holders_org_user ON custody_holders(organization_id, user_id) WHERE user_id IS NOT NULL;`
* `CREATE INDEX IF NOT EXISTS idx_custody_holders_org_email ON custody_holders(organization_id, invited_email);`
* `CREATE INDEX IF NOT EXISTS idx_custody_holders_department ON custody_holders(department_id);`

### `certifications` (Certifications & Permits Catalog)
Types of certifications defined by the organization (e.g., "Forklift Driving", "Radio Operator", "Working at Heights"). Includes automatic `updated_at` trigger (`trg_certifications_updated_at`).

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Certification identifier |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id) ON DELETE CASCADE` | Organization association |
| `name` | `TEXT` | `NOT NULL` | Certification name |
| `description` | `TEXT` | `NULLABLE` | Description / prerequisites |
| `validity_months` | `INTEGER` | `NULLABLE, CHECK (validity_months IS NULL OR validity_months > 0)` | Validity in months (must be positive if set) |
| `created_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Addition timestamp (ISO8601 UTC) |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Certification definition update timestamp (ISO8601 UTC) |

*Indexes:*
* `CREATE INDEX IF NOT EXISTS idx_certifications_org ON certifications(organization_id);`
* `CREATE UNIQUE INDEX IF NOT EXISTS idx_certifications_org_name ON certifications(organization_id, name);`

### `holder_certifications` (Holder Certifications)
A junction table representing active certificates. A record may be updated upon validity extension. Includes automatic `updated_at` trigger (`trg_holder_certifications_updated_at`).

*Table-level constraints:*
* `UNIQUE (holder_id, certification_id)` (prevents duplicate certification assignments for the same holder)
* `CHECK (expires_at IS NULL OR expires_at >= acquired_at)`

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Record identifier |
| `holder_id` | `UUID / TEXT` | `NOT NULL, REFERENCES custody_holders(id) ON DELETE RESTRICT` | Identifier of the person holding the certification |
| `certification_id` | `UUID / TEXT` | `NOT NULL, REFERENCES certifications(id) ON DELETE RESTRICT` | Certification identifier |
| `acquired_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL` | Date certification was acquired |
| `expires_at` | `TIMESTAMPTZ / TEXT` | `NULLABLE` | Expiration date (if applicable, `>= acquired_at`) |
| `certificate_number` | `TEXT` | `NULLABLE` | Certificate / license number |
| `notes` | `TEXT` | `NULLABLE` | Notes |
| `created_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Record creation timestamp (ISO8601 UTC) |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Certificate details update timestamp (ISO8601 UTC) |

*Indexes:*
* `CREATE INDEX IF NOT EXISTS idx_holder_cert_holder ON holder_certifications(holder_id);`
* `CREATE INDEX IF NOT EXISTS idx_holder_cert_cert ON holder_certifications(certification_id);`

---

## 5. Equipment Catalog & Inventory

### `item_categories` (Equipment Categories)
An organization-level managed category catalog to prevent typos and duplicates (e.g., "Computing", "Communications", "Tools", "Protective Gear"). Includes automatic `updated_at` trigger (`trg_item_categories_updated_at`).

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Category identifier |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id) ON DELETE CASCADE` | Organization association |
| `name` | `TEXT` | `NOT NULL` | Category name |
| `description` | `TEXT` | `NULLABLE` | Description |
| `created_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Creation timestamp (ISO8601 UTC) |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Category name/description update timestamp (ISO8601 UTC) |

*Indexes:*
* `CREATE INDEX IF NOT EXISTS idx_item_categories_org ON item_categories(organization_id);`
* `CREATE UNIQUE INDEX IF NOT EXISTS idx_item_categories_org_name ON item_categories(organization_id, name);`

### `item_types` (Catalog Item Types)
Includes automatic `updated_at` trigger (`trg_item_types_updated_at`).

*Tracking Type Mapping (`tracking_type`):*
* `0` = Quantitative (simple inventory counting)
* `1` = Serialized (individual item management with serial number and barcode)

*Table-level constraints:*
* `UNIQUE (organization_id, name)` (prevents duplicate equipment definitions per organization)

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Item type identifier |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id) ON DELETE CASCADE` | Organization association |
| `category_id` | `UUID / TEXT` | `NULLABLE, REFERENCES item_categories(id) ON DELETE SET NULL` | Managed category association |
| `name` | `TEXT` | `NOT NULL` | Model / item name |
| `tracking_type` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 1, CHECK (tracking_type IN (0, 1))` | `0` = Quantitative, `1` = Serialized |
| `unit_of_measure` | `TEXT` | `NOT NULL, DEFAULT 'unit'` | Unit of measure |
| `description` | `TEXT` | `NULLABLE` | Specifications and notes |
| `created_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Creation timestamp (ISO8601 UTC) |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Item specification update timestamp (ISO8601 UTC) |

*Indexes:*
* `CREATE INDEX IF NOT EXISTS idx_item_types_org ON item_types(organization_id);`
* `CREATE INDEX IF NOT EXISTS idx_item_types_category ON item_types(category_id);`

### `serialized_items` (Unique Serialized Equipment)
A dynamic state entity combining legal custody (`current_holder_id`) alongside physical location.
Physical location is flexible (OR principle): you can specify a registered entity (`location_holder_id`), free text (`location_custom_text`), or both together (e.g., entity "Warehouse B" + free text "Cabinet 4, Top Shelf"). Includes automatic `updated_at` trigger (`trg_serialized_items_updated_at`).

*Item Status Mapping (`status`):*
* `0` = Active and operational (`ACTIVE`)
* `1` = In maintenance / faulty (`MAINTENANCE`)
* `2` = Lost / missing (`LOST`)
* `3` = Decommissioned / retired (`DECOMMISSIONED`)

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Unique item identifier |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id) ON DELETE RESTRICT` | Organization association |
| `item_type_id` | `UUID / TEXT` | `NOT NULL, REFERENCES item_types(id) ON DELETE RESTRICT` | Catalog item type |
| `serial_number` | `TEXT` | `NULLABLE` | Manufacturer serial number |
| `barcode` | `TEXT` | `NOT NULL` | Barcode / QR value for physical scanning |
| `current_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id) ON DELETE RESTRICT` | Official responsible custodian (person or main warehouse) |
| `location_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id) ON DELETE RESTRICT` | Physical location as a registered entity (warehouse, room, vehicle) |
| `location_custom_text` | `TEXT` | `NULLABLE` | Physical location as free text (specific location detail) |
| `status` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 0, CHECK (status IN (0, 1, 2, 3))` | Item condition status |
| `created_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | System intake timestamp (ISO8601 UTC) |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Status/holder/location update timestamp (ISO8601 UTC) |

*Indexes & Unique Constraints:*
* `CREATE INDEX IF NOT EXISTS idx_serialized_items_org ON serialized_items(organization_id);`
* `CREATE UNIQUE INDEX IF NOT EXISTS idx_serialized_org_barcode ON serialized_items(organization_id, barcode);`
* `CREATE UNIQUE INDEX IF NOT EXISTS idx_serialized_org_serial ON serialized_items(organization_id, serial_number) WHERE serial_number IS NOT NULL;`
* `CREATE INDEX IF NOT EXISTS idx_serialized_current_holder ON serialized_items(current_holder_id);`
* `CREATE INDEX IF NOT EXISTS idx_serialized_location_holder ON serialized_items(location_holder_id);`
* `CREATE INDEX IF NOT EXISTS idx_serialized_item_type ON serialized_items(item_type_id);`

### `inventory_stocks` (Quantitative Inventory)
Dynamic quantity aggregation for quantitative items (`tracking_type = 0`). Includes automatic `updated_at` trigger (`trg_inventory_stocks_updated_at`).

*Table-level constraints:*
* `CHECK (quantity >= 0)`

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Stock row identifier |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id) ON DELETE RESTRICT` | Organization association |
| `item_type_id` | `UUID / TEXT` | `NOT NULL, REFERENCES item_types(id) ON DELETE RESTRICT` | Catalog association |
| `holder_id` | `UUID / TEXT` | `NOT NULL, REFERENCES custody_holders(id) ON DELETE RESTRICT` | Stock holder (warehouse or person) |
| `quantity` | `INTEGER` | `NOT NULL, DEFAULT 0` | Quantity in stock (`>= 0`) |
| `created_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Initial stock row creation timestamp (ISO8601 UTC) |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Last quantity balance change timestamp (ISO8601 UTC) |

*Indexes & Constraints:*
* `CREATE UNIQUE INDEX IF NOT EXISTS idx_inventory_stocks_holder_item ON inventory_stocks(holder_id, item_type_id);`
* `CREATE INDEX IF NOT EXISTS idx_inventory_stocks_org ON inventory_stocks(organization_id);`

---

## 6. Predefined Transfer Templates (Transfer Templates / Kits)

Templates that allow defining "equipment kits" or fixed recurring transfers (e.g., "New Employee Onboarding Kit", "Field Activity Deployment Kit").
The template defines requested products and quantities. When activating the template for an actual transfer, the admin selects or scans the specific serialized items to be transferred.

### `transfer_templates` (Transfer Template Header)
Includes automatic `updated_at` trigger (`trg_transfer_templates_updated_at`).

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Template identifier |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id) ON DELETE CASCADE` | Organization association |
| `name` | `TEXT` | `NOT NULL` | Template name (e.g., "Site Manager Equipment Set") |
| `description` | `TEXT` | `NULLABLE` | Template description and uses |
| `default_source_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id) ON DELETE RESTRICT` | Optional default source (e.g., main warehouse) |
| `default_dest_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id) ON DELETE RESTRICT` | Optional default destination |
| `is_active` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 1, CHECK (is_active IN (0, 1))` | `1` = Active, `0` = Inactive |
| `created_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Creation timestamp (ISO8601 UTC) |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Template settings update timestamp (ISO8601 UTC) |

*Indexes:*
* `CREATE UNIQUE INDEX IF NOT EXISTS idx_transfer_templates_org_name ON transfer_templates(organization_id, name);`

### `transfer_template_lines` (Template Item Lines)
Immutable template line definitions (no `updated_at` column or trigger).

*Table-level constraints:*
* Composite primary key: `PRIMARY KEY (template_id, line_number)`
* `UNIQUE (template_id, item_type_id)` (prevents duplicate item types within a template)
* `CHECK (quantity > 0)`

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `template_id` | `UUID / TEXT` | `NOT NULL, REFERENCES transfer_templates(id) ON DELETE CASCADE` | Parent template identifier |
| `line_number` | `INTEGER` | `NOT NULL` | Sequential line number within the template (1, 2, 3...) |
| `item_type_id` | `UUID / TEXT` | `NOT NULL, REFERENCES item_types(id) ON DELETE RESTRICT` | Catalog product type |
| `quantity` | `INTEGER` | `NOT NULL, DEFAULT 1` | Required quantity of the product (`> 0`) |

*Indexes:*
* The primary index is automatically created on `(template_id, line_number)`.
* `CREATE INDEX IF NOT EXISTS idx_template_lines_item_type ON transfer_template_lines(item_type_id);`

---

## 7. Immutable Activity Logs

Every custody (ownership) change or physical location change is stored strictly as append-only records. These rows do not include `updated_at` and have no `updated_at` triggers.

### `custody_transfers` (Transfer Header)

*Strict business constraints (`CHECK`):*
* When `source_holder_id` or `dest_holder_id` is `NULL`, `notes` **must contain a non-empty value**:
  `CHECK ((source_holder_id IS NOT NULL AND dest_holder_id IS NOT NULL) OR (notes IS NOT NULL AND TRIM(notes) != ''))`
* Prevent self-transfers when both source and destination are specified:
  `CHECK (source_holder_id IS NULL OR dest_holder_id IS NULL OR source_holder_id <> dest_holder_id)`

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Internal transfer identifier |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id) ON DELETE RESTRICT` | Organization association |
| `transfer_template_id` | `UUID / TEXT` | `NULLABLE, REFERENCES transfer_templates(id) ON DELETE SET NULL` | Template association if created from a template |
| `transfer_number` | `TEXT` | `NOT NULL` | Unique voucher reference number in organization (e.g., "TR-2026-00042") |
| `admin_holder_id` | `UUID / TEXT` | `NOT NULL, REFERENCES custody_holders(id) ON DELETE RESTRICT` | Admin who executed and authorized the transfer |
| `source_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id) ON DELETE RESTRICT` | Source holder (`NULL` = External Intake) |
| `dest_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id) ON DELETE RESTRICT` | Destination holder (`NULL` = Disposal / External Loan) |
| `notes` | `TEXT` | `NULLABLE` | Mandatory if source or destination is `NULL` |
| `transferred_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Final transfer execution timestamp (ISO8601 UTC) |

*Indexes & Constraints:*
* `CREATE UNIQUE INDEX IF NOT EXISTS idx_transfers_org_number ON custody_transfers(organization_id, transfer_number);`
* `CREATE INDEX IF NOT EXISTS idx_transfers_source ON custody_transfers(source_holder_id);`
* `CREATE INDEX IF NOT EXISTS idx_transfers_dest ON custody_transfers(dest_holder_id);`
* `CREATE INDEX IF NOT EXISTS idx_transfers_org_date ON custody_transfers(organization_id, transferred_at DESC);`

### `custody_transfer_lines` (Transfer Lines)

Weak entity dependent on the parent transfer (append-only, no `updated_at` or trigger).
* Composite primary key: `PRIMARY KEY (transfer_id, line_number)`
* `CHECK` constraint (XOR validation & serialized quantity = 1):
  `CHECK ((serialized_item_id IS NOT NULL AND item_type_id IS NULL AND quantity = 1) OR (serialized_item_id IS NULL AND item_type_id IS NOT NULL AND quantity > 0))`

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `transfer_id` | `UUID / TEXT` | `NOT NULL, REFERENCES custody_transfers(id) ON DELETE CASCADE` | Parent transfer identifier (part of primary key) |
| `line_number` | `INTEGER` | `NOT NULL` | Sequential line number within the transfer (1, 2, 3...) |
| `serialized_item_id` | `UUID / TEXT` | `NULLABLE, REFERENCES serialized_items(id) ON DELETE RESTRICT` | Specific serialized item (if applicable) |
| `item_type_id` | `UUID / TEXT` | `NULLABLE, REFERENCES item_types(id) ON DELETE RESTRICT` | Quantitative item type (if applicable) |
| `quantity` | `INTEGER` | `NOT NULL, DEFAULT 1` | Transferred quantity (for serialized: always 1; for quantitative: `> 0`) |

*Indexes:*
* The primary index is automatically created on `(transfer_id, line_number)`.
* `CREATE INDEX IF NOT EXISTS idx_transfer_lines_serialized_item ON custody_transfer_lines(serialized_item_id);`

### `serialized_item_location_logs` (Physical Location Changes Only – Location Audit Log)

Records a change in the physical location of a serialized product without a change in ownership/custody (e.g., moving from Shelf A to Shelf B, moving to a testing room, or a momentary loan). Append-only, no `updated_at` or trigger.
* `CHECK` constraint for destination: At least one of the fields (location entity or non-empty trimmed text) must be set:
  `CHECK (to_location_holder_id IS NOT NULL OR (to_location_custom_text IS NOT NULL AND TRIM(to_location_custom_text) <> ''))`

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Unique identifier of the location change record |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id) ON DELETE RESTRICT` | Organization association |
| `serialized_item_id` | `UUID / TEXT` | `NOT NULL, REFERENCES serialized_items(id) ON DELETE RESTRICT` | The item that was moved |
| `admin_holder_id` | `UUID / TEXT` | `NOT NULL, REFERENCES custody_holders(id) ON DELETE RESTRICT` | User/admin who updated the location |
| `from_location_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id) ON DELETE RESTRICT` | Previous entity location |
| `from_location_custom_text` | `TEXT` | `NULLABLE` | Previous free-text location |
| `to_location_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id) ON DELETE RESTRICT` | New entity location (if a registered warehouse/room was selected) |
| `to_location_custom_text` | `TEXT` | `NULLABLE` | New free-text location |
| `notes` | `TEXT` | `NULLABLE` | Reason for move / additional notes |
| `recorded_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Exact timestamp of the location change (ISO8601 UTC) |

*Indexes:*
* `CREATE INDEX IF NOT EXISTS idx_location_logs_item ON serialized_item_location_logs(serialized_item_id);`
* `CREATE INDEX IF NOT EXISTS idx_location_logs_org_date ON serialized_item_location_logs(organization_id, recorded_at);`

---

## 8. Field Audits & Scan Checklists (Field Audits & Item Records)

The audit model is based on a **Pre-populated Checklist** generated when the session opens:
1. When opening an audit session (for the entire organization or scoped to a department/holders), the system generates records in `audit_item_records` for all serialized items required for inspection in this session, with an initial status of `0` (Pending scan).
2. At any given moment, audit progress is calculated directly: `(Scanned / Total records in session) * 100`.
3. When a user scans an item:
   * If the item exists in the session: the record is updated to status `1` (Verified) or `2` (Custody mismatch), along with the scanner's ID and scan timestamp.
   * If an item is scanned that was not defined in the original checklist: a row is created with status `3` (Unexpected item scanned).

### `audit_sessions` (Audit Sessions)
Includes automatic `updated_at` trigger (`trg_audit_sessions_updated_at`).

*Audit Status Mapping (`status`):*
* `0` = Open for scanning (`OPEN`)
* `1` = Closed (`CLOSED`)
* `2` = Archived (`ARCHIVED`)

*Table-level constraints:*
* `CHECK (closed_at IS NULL OR closed_at >= opened_at)`

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Audit session identifier |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id) ON DELETE CASCADE` | Organization association |
| `title` | `TEXT` | `NOT NULL` | Audit title (e.g., "Quarterly Equipment Inspection - Operations Dept") |
| `initiated_by_holder_id` | `UUID / TEXT` | `NOT NULL, REFERENCES custody_holders(id) ON DELETE RESTRICT` | Admin who opened the audit |
| `status` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 0, CHECK (status IN (0, 1, 2))` | Audit status |
| `opened_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Opening timestamp (ISO8601 UTC) |
| `closed_at` | `TIMESTAMPTZ / TEXT` | `NULLABLE` | Closing timestamp (`>= opened_at` if set) |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Audit status/title update timestamp (ISO8601 UTC) |

*Indexes:*
* `CREATE INDEX IF NOT EXISTS idx_audit_sessions_org ON audit_sessions(organization_id);`

### `audit_item_records` (Scan Tracking Records for Each Item in Session)
Includes automatic `updated_at` trigger (`trg_audit_item_records_updated_at`).

*Scan Status Mapping (`scan_status`):*
* `0` = Pending scan (`PENDING`) - created when opening the audit, not yet scanned
* `1` = Verified (`VERIFIED`) - scanned by the expected registered custodian
* `2` = Custody mismatch (`MISMATCH`) - scanned by someone other than the registered custodian
* `3` = Unexpected item (`UNEXPECTED`) - item not designated for scanning in this session that was actually scanned

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Unique tracking row identifier |
| `audit_session_id` | `UUID / TEXT` | `NOT NULL, REFERENCES audit_sessions(id) ON DELETE CASCADE` | Association to specific audit session |
| `serialized_item_id` | `UUID / TEXT` | `NOT NULL, REFERENCES serialized_items(id) ON DELETE RESTRICT` | Audited serialized item |
| `expected_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id) ON DELETE RESTRICT` | Registered holder in system when session opened |
| `scan_status` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 0, CHECK (scan_status IN (0, 1, 2, 3))` | Current tracking status (`0`=Pending, `1`=Verified, `2`=Mismatch, `3`=Unexpected) |
| `scanned_by_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id) ON DELETE RESTRICT` | Person who actually performed the scan |
| `scanned_at` | `TIMESTAMPTZ / TEXT` | `NULLABLE` | Exact timestamp when item was physically scanned |
| `resolved_by_admin_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id) ON DELETE RESTRICT` | Admin who approved/resolved a discrepancy (if any) |
| `resolution_notes` | `TEXT` | `NULLABLE` | Reasoning or action taken following a discrepancy |
| `created_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Record creation timestamp (ISO8601 UTC) |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Last update timestamp (ISO8601 UTC) |

*Indexes & Constraints:*
* `CREATE UNIQUE INDEX IF NOT EXISTS idx_audit_item_session_item ON audit_item_records(audit_session_id, serialized_item_id);`
* `CREATE INDEX IF NOT EXISTS idx_audit_item_session_status ON audit_item_records(audit_session_id, scan_status);`
* `CREATE INDEX IF NOT EXISTS idx_audit_item_holder ON audit_item_records(scanned_by_holder_id);`

---

## 9. Auth Sessions & API Idempotency (Immutable Tables)

Both tables below are append-only / immutable and do **not** include `updated_at` or `updated_at` triggers.

### `user_sessions` (Authenticated User Sessions)

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `token_hash` | `TEXT` | `PRIMARY KEY` | Hashed session token |
| `user_id` | `UUID / TEXT` | `NOT NULL, REFERENCES users(id) ON DELETE CASCADE` | Authenticated user |
| `active_org_id` | `UUID / TEXT` | `NULLABLE, REFERENCES organizations(id) ON DELETE SET NULL` | Currently selected active organization |
| `expires_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL` | Session expiration timestamp |
| `created_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Session creation timestamp (ISO8601 UTC) |

*Indexes:*
* `CREATE INDEX IF NOT EXISTS idx_user_sessions_user ON user_sessions(user_id);`
* `CREATE INDEX IF NOT EXISTS idx_user_sessions_expires ON user_sessions(expires_at);`

### `idempotency_keys` (API Mutation Deduplication)
Composite primary key: `PRIMARY KEY (organization_id, idempotency_key, endpoint_name)`.

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id) ON DELETE RESTRICT` | Tenant scope |
| `idempotency_key` | `TEXT` | `NOT NULL` | Client-supplied idempotency key |
| `endpoint_name` | `TEXT` | `NOT NULL` | API endpoint identifier |
| `response_json` | `TEXT` | `NOT NULL` | Cached JSON response payload |
| `created_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL, DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))` | Record creation timestamp (ISO8601 UTC) |

---

## 10. `updated_at` Automation Triggers

All 13 mutable tables maintain `updated_at` automatically on `UPDATE` using the following SQLite trigger pattern (firing only when the caller did not already modify `updated_at`):

```sql
CREATE TRIGGER IF NOT EXISTS trg_<table>_updated_at
AFTER UPDATE ON <table>
WHEN NEW.updated_at = OLD.updated_at
BEGIN
    UPDATE <table>
    SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now')
    WHERE id = OLD.id;
END;
```

* **Mutable tables with `updated_at` triggers:** `users`, `organizations`, `departments`, `custody_holders`, `certifications`, `holder_certifications`, `item_categories`, `item_types`, `serialized_items`, `inventory_stocks`, `transfer_templates`, `audit_sessions`, `audit_item_records`.
* **Immutable tables without `updated_at` or triggers:** `custody_transfers`, `custody_transfer_lines`, `serialized_item_location_logs`, `user_sessions`, `idempotency_keys`, `transfer_template_lines`.
