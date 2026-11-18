# Data Model & Schema - Rashmatz

A unified, efficient, and modern relational data architecture. 100% compatible with both **PostgreSQL** and **SQLite** without requiring substantial schema changes.

## 1. Cross-DB Compatibility & Optimization Principles

1. **Identifiers (Primary Keys):** Use of UUIDs (or time-sorted UUIDv7). In PostgreSQL the type is `UUID`, and in SQLite the type is `TEXT`. These identifiers prevent data exposure and allow offline ID generation on the client side.
2. **Strings:** Use of standard `TEXT` type (in PostgreSQL and SQLite there is no advantage to `VARCHAR(n)`).
3. **Compact Statuses & Enums:** Managed as small integers (`SMALLINT` in Postgres / `INTEGER` in SQLite) occupying only 1–2 bytes.
4. **Unified Custody Holder Entity (`custody_holders`):** Eliminating the separation between a person and a location. Every actor that can hold equipment (employee, contractor, warehouse, vehicle, shelf) is represented in a single entity.
5. **Distinction Between Custody and Physical Location (Flexible OR Principle):**
   * **Custody:** Defines legal/managerial accountability for the item (`current_holder_id`).
   * **Physical Location:** Can be defined as a registered entity in the system (`location_holder_id`), as free text (`location_custom_text`), or **as both together** (e.g., entity "Main Warehouse" + text detail "Cabinet 3, Drawer B").
   * Every location update without a transfer of ownership is recorded in a dedicated immutable log.
6. **Predefined Transfer Templates (Transfer Templates / Kits):** Allow defining "equipment kits" or preset recurring transfers (e.g., employee onboarding kit, field deployment kit), with optional source/destination and a list of products and quantities. When executing an actual transfer from a template, the specific serialized items are selected or scanned.
7. **Change Tracking (`updated_at` vs. Append-Only):** SQL engines do not maintain automatic row history. Therefore, mutable state tables (Mutable Entities) include an `updated_at` column. Immutable log tables (Append-only / Immutable logs, such as ownership transfers and location movements) do not include this field, only a creation timestamp.
8. **Lean Indexing:** Removal of redundant indexes on low-cardinality columns (such as single status columns). Retaining indexes only for active FKs in JOINs and business `UNIQUE` indexes.

---

## 2. Unified High-Level ERD

```
User (Global Google Auth)
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
        └── AuditSession
              └── AuditItemRecord (Pre-populated checklist & scan execution)
```

---

## 3. Identity & Tenancy Management

### `users` (Global Google User Accounts)
Users authenticated via Google OAuth. They do not belong directly to a single organization.

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Global unique identifier |
| `google_id` | `TEXT` | `UNIQUE, NOT NULL` | Permanent Google user ID (`sub`) |
| `email` | `TEXT` | `UNIQUE, NOT NULL` | Email address (Lowercased) |
| `full_name` | `TEXT` | `NOT NULL` | Full name as configured in Google account |
| `avatar_url` | `TEXT` | `NULLABLE` | Google profile picture |
| `created_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Initial creation timestamp |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Last update timestamp |

*Indexes:*
* `CREATE UNIQUE INDEX idx_users_google_id ON users(google_id);`
* `CREATE UNIQUE INDEX idx_users_email ON users(email);`

### `organizations` (Tenants)

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Organization identifier |
| `name` | `TEXT` | `NOT NULL` | Organization name |
| `slug` | `TEXT` | `UNIQUE, NOT NULL` | Textual identifier for routes and access |
| `is_active` | `INTEGER` | `NOT NULL, DEFAULT 1` | `1` = Active, `0` = Disabled |
| `created_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Organization creation timestamp |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Organization settings update timestamp |

*Indexes:*
* `CREATE UNIQUE INDEX idx_organizations_slug ON organizations(slug);`

### `departments` (Organization Departments / Divisions / Units)
Internal organizational structure for assigning employees, teams, and dedicated warehouses.

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Unique department identifier |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id)` | Organization association |
| `name` | `TEXT` | `NOT NULL` | Department / team name |
| `code` | `TEXT` | `NULLABLE` | Short identifier code (e.g., "OPS", "LOG") |
| `created_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Creation timestamp |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Details update timestamp |

*Indexes:*
* `CREATE UNIQUE INDEX idx_departments_org_name ON departments(organization_id, name);`

---

## 4. Unified Custody Holder, Attendance & Certifications (Custodians, Attendance & Certifications)

### `custody_holders` (Equipment Holder – Person or Location)
A unified table consolidating people and locations. Also serves as the authorized Whitelist for the organization and for tracking attendance of organization personnel.

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
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id)` | Organization association |
| `department_id` | `UUID / TEXT` | `NULLABLE, REFERENCES departments(id)` | Department / team association in organization |
| `user_id` | `UUID / TEXT` | `NULLABLE, REFERENCES users(id)` | Link to authenticated Google account (if a person) |
| `holder_type` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 1` | `0` = Location, `1` = Person |
| `name` | `TEXT` | `NOT NULL` | Name of the person or location |
| `internal_identifier` | `TEXT` | `NULLABLE` | Internal identifier (employee number, warehouse code) |
| `invited_email` | `TEXT` | `NULLABLE` | Whitelist email (for people) |
| `role` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 0` | Permission level in organization (`0` = None, `1` = Member, `2` = Admin) |
| `attendance_status` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 0` | Attendance status (`0` = Inactive, `1` = Present, `2` = Home) |
| `phone_number` | `TEXT` | `NULLABLE` | Contact phone number |
| `is_active` | `INTEGER` | `NOT NULL, DEFAULT 1` | `1` = Active, `0` = Disabled |
| `created_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Addition timestamp |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Last update timestamp |

*Indexes & Constraints:*
* `CREATE UNIQUE INDEX idx_custody_holders_org_internal ON custody_holders(organization_id, internal_identifier) WHERE internal_identifier IS NOT NULL;`
* `CREATE UNIQUE INDEX idx_custody_holders_org_user ON custody_holders(organization_id, user_id) WHERE user_id IS NOT NULL;`
* `CREATE INDEX idx_custody_holders_org_email ON custody_holders(organization_id, invited_email);`
* `CREATE INDEX idx_custody_holders_department ON custody_holders(department_id);`

### `certifications` (Certifications & Permits Catalog)
Types of certifications defined by the organization (e.g., "Forklift Driving", "Radio Operator", "Working at Heights").

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Certification identifier |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id)` | Organization association |
| `name` | `TEXT` | `NOT NULL` | Certification name |
| `description` | `TEXT` | `NULLABLE` | Description / prerequisites |
| `validity_months` | `INTEGER` | `NULLABLE` | Validity in months (if periodic renewal is required) |
| `created_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Addition timestamp |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Certification definition update timestamp |

*Indexes:*
* `CREATE UNIQUE INDEX idx_certifications_org_name ON certifications(organization_id, name);`

### `holder_certifications` (Holder Certifications)
A junction table representing active certificates. A record may be updated upon validity extension.

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Record identifier |
| `holder_id` | `UUID / TEXT` | `NOT NULL, REFERENCES custody_holders(id)` | Identifier of the person holding the certification |
| `certification_id` | `UUID / TEXT` | `NOT NULL, REFERENCES certifications(id)` | Certification identifier |
| `acquired_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL` | Date certification was acquired |
| `expires_at` | `TIMESTAMPTZ / TEXT` | `NULLABLE` | Expiration date (if applicable) |
| `certificate_number` | `TEXT` | `NULLABLE` | Certificate / license number |
| `notes` | `TEXT` | `NULLABLE` | Notes |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Certificate details update timestamp |

*Indexes:*
* `CREATE INDEX idx_holder_cert_holder ON holder_certifications(holder_id);`
* `CREATE INDEX idx_holder_cert_cert ON holder_certifications(certification_id);`

---

## 5. Equipment Catalog & Inventory

### `item_categories` (Equipment Categories)
An organization-level managed category catalog to prevent typos and duplicates (e.g., "Computing", "Communications", "Tools", "Protective Gear").

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Category identifier |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id)` | Organization association |
| `name` | `TEXT` | `NOT NULL` | Category name |
| `description` | `TEXT` | `NULLABLE` | Description |
| `created_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Creation timestamp |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Category name/description update timestamp |

*Indexes:*
* `CREATE UNIQUE INDEX idx_item_categories_org_name ON item_categories(organization_id, name);`

### `item_types` (Catalog Item Types)

*Tracking Type Mapping (`tracking_type`):*
* `0` = Quantitative (simple inventory counting)
* `1` = Serialized (individual item management with serial number and barcode)

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Item type identifier |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id)` | Organization association |
| `category_id` | `UUID / TEXT` | `NULLABLE, REFERENCES item_categories(id)` | Managed category association |
| `name` | `TEXT` | `NOT NULL` | Model / item name |
| `tracking_type` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 1` | `0` = Quantitative, `1` = Serialized |
| `unit_of_measure` | `TEXT` | `DEFAULT 'unit'` | Unit of measure |
| `description` | `TEXT` | `NULLABLE` | Specifications and notes |
| `created_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Creation timestamp |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Item specification update timestamp |

*Indexes:*
* `CREATE INDEX idx_item_types_org ON item_types(organization_id);`
* `CREATE INDEX idx_item_types_category ON item_types(category_id);`

### `serialized_items` (Unique Serialized Equipment)
A dynamic state entity combining legal custody (`current_holder_id`) alongside physical location.
Physical location is flexible (OR principle): you can specify a registered entity (`location_holder_id`), free text (`location_custom_text`), or both together (e.g., entity "Warehouse B" + free text "Cabinet 4, Top Shelf").

*Item Status Mapping (`status`):*
* `0` = Active and operational (`ACTIVE`)
* `1` = In maintenance / faulty (`MAINTENANCE`)
* `2` = Lost / missing (`LOST`)
* `3` = Decommissioned / retired (`DECOMMISSIONED`)

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Unique item identifier |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id)` | Organization association |
| `item_type_id` | `UUID / TEXT` | `NOT NULL, REFERENCES item_types(id)` | Catalog item type |
| `serial_number` | `TEXT` | `NULLABLE` | Manufacturer serial number |
| `barcode` | `TEXT` | `NOT NULL` | Barcode / QR value for physical scanning |
| `current_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id)` | Official responsible custodian (person or main warehouse) |
| `location_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id)` | Physical location as a registered entity (warehouse, room, vehicle) |
| `location_custom_text` | `TEXT` | `NULLABLE` | Physical location as free text (specific location detail) |
| `status` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 0` | Item condition status |
| `created_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | System intake timestamp |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Status/holder/location update timestamp |

*Indexes & Unique Constraints:*
* `CREATE UNIQUE INDEX idx_serialized_org_barcode ON serialized_items(organization_id, barcode);`
* `CREATE UNIQUE INDEX idx_serialized_org_serial ON serialized_items(organization_id, serial_number) WHERE serial_number IS NOT NULL;`
* `CREATE INDEX idx_serialized_current_holder ON serialized_items(current_holder_id);`
* `CREATE INDEX idx_serialized_location_holder ON serialized_items(location_holder_id);`
* `CREATE INDEX idx_serialized_item_type ON serialized_items(item_type_id);`

### `inventory_stocks` (Quantitative Inventory)
Dynamic quantity aggregation for quantitative items (`tracking_type = 0`).

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Stock row identifier |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id)` | Organization association |
| `item_type_id` | `UUID / TEXT` | `NOT NULL, REFERENCES item_types(id)` | Catalog association |
| `holder_id` | `UUID / TEXT` | `NOT NULL, REFERENCES custody_holders(id)` | Stock holder (warehouse or person) |
| `quantity` | `INTEGER` | `NOT NULL, DEFAULT 0` | Quantity in stock |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Last quantity balance change timestamp |

*Indexes & Constraints:*
* `CREATE UNIQUE INDEX idx_inventory_stocks_holder_item ON inventory_stocks(holder_id, item_type_id);`
* `CREATE INDEX idx_inventory_stocks_org ON inventory_stocks(organization_id);`

---

## 6. Predefined Transfer Templates (Transfer Templates / Kits)

Templates that allow defining "equipment kits" or fixed recurring transfers (e.g., "New Employee Onboarding Kit", "Field Activity Deployment Kit").
The template defines requested products and quantities. When activating the template for an actual transfer, the admin selects or scans the specific serialized items to be transferred.

### `transfer_templates` (Transfer Template Header)

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Template identifier |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id)` | Organization association |
| `name` | `TEXT` | `NOT NULL` | Template name (e.g., "Site Manager Equipment Set") |
| `description` | `TEXT` | `NULLABLE` | Template description and uses |
| `default_source_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id)` | Optional default source (e.g., main warehouse) |
| `default_dest_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id)` | Optional default destination |
| `is_active` | `INTEGER` | `NOT NULL, DEFAULT 1` | `1` = Active, `0` = Inactive |
| `created_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Creation timestamp |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Template settings update timestamp |

*Indexes:*
* `CREATE UNIQUE INDEX idx_transfer_templates_org_name ON transfer_templates(organization_id, name);`

### `transfer_template_lines` (Template Item Lines)

Composite primary key: `PRIMARY KEY (template_id, line_number)`.

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `template_id` | `UUID / TEXT` | `NOT NULL, REFERENCES transfer_templates(id) ON DELETE CASCADE` | Parent template identifier |
| `line_number` | `INTEGER` | `NOT NULL` | Sequential line number within the template (1, 2, 3...) |
| `item_type_id` | `UUID / TEXT` | `NOT NULL, REFERENCES item_types(id)` | Catalog product type |
| `quantity` | `INTEGER` | `NOT NULL, DEFAULT 1` | Required quantity of the product (for serialized or quantitative) |

*Indexes:*
* The primary index is automatically created on `(template_id, line_number)`.
* `CREATE INDEX idx_template_lines_item_type ON transfer_template_lines(item_type_id);`

---

## 7. Immutable Activity Logs

Every custody (ownership) change or physical location change is stored strictly as Append-only records. These rows do not include `updated_at`.

### `custody_transfers` (Transfer Header)

* Strict business constraint: When `source_holder_id` or `dest_holder_id` is equal to `NULL`, the `notes` field **must contain a value** (non-empty).
* `CHECK` constraint: `CHECK ((source_holder_id IS NOT NULL AND dest_holder_id IS NOT NULL) OR (notes IS NOT NULL AND TRIM(notes) != ''))`

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Internal transfer identifier |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id)` | Organization association |
| `transfer_template_id` | `UUID / TEXT` | `NULLABLE, REFERENCES transfer_templates(id)` | Template association if created from a template |
| `transfer_number` | `TEXT` | `NOT NULL` | Unique voucher reference number in organization (e.g., "TR-2026-00042") |
| `admin_holder_id` | `UUID / TEXT` | `NOT NULL, REFERENCES custody_holders(id)` | Admin who executed and authorized the transfer |
| `source_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id)` | Source holder (`NULL` = External Intake) |
| `dest_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id)` | Destination holder (`NULL` = Disposal / External Loan) |
| `notes` | `TEXT` | `NULLABLE` | Mandatory if source or destination is `NULL` |
| `transferred_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Final transfer execution timestamp |

*Indexes & Constraints:*
* `CREATE UNIQUE INDEX idx_transfers_org_number ON custody_transfers(organization_id, transfer_number);`
* `CREATE INDEX idx_transfers_source ON custody_transfers(source_holder_id);`
* `CREATE INDEX idx_transfers_dest ON custody_transfers(dest_holder_id);`
* `CREATE INDEX idx_transfers_org_date ON custody_transfers(organization_id, transferred_at DESC);`

### `custody_transfer_lines` (Transfer Lines)

Weak entity dependent on the parent transfer.
* Composite primary key: `PRIMARY KEY (transfer_id, line_number)`.
* `CHECK` constraint: Exactly one of the two fields is set: `(serialized_item_id IS NOT NULL AND item_type_id IS NULL) OR (serialized_item_id IS NULL AND item_type_id IS NOT NULL)`
* `CHECK` constraint: For a serialized item `quantity = 1`.

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `transfer_id` | `UUID / TEXT` | `NOT NULL, REFERENCES custody_transfers(id) ON DELETE CASCADE` | Parent transfer identifier (part of primary key) |
| `line_number` | `INTEGER` | `NOT NULL` | Sequential line number within the transfer (1, 2, 3...) |
| `serialized_item_id` | `UUID / TEXT` | `NULLABLE, REFERENCES serialized_items(id)` | Specific serialized item (if applicable) |
| `item_type_id` | `UUID / TEXT` | `NULLABLE, REFERENCES item_types(id)` | Quantitative item type (if applicable) |
| `quantity` | `INTEGER` | `NOT NULL, DEFAULT 1` | Transferred quantity (for serialized: always 1) |

*Indexes:*
* The primary index is automatically created on `(transfer_id, line_number)`.
* `CREATE INDEX idx_transfer_lines_serialized_item ON custody_transfer_lines(serialized_item_id);`

### `serialized_item_location_logs` (Physical Location Changes Only – Location Audit Log)

Records a change in the physical location of a serialized product without a change in ownership/custody (e.g., moving from Shelf A to Shelf B, moving to a testing room, or a momentary loan).
* `CHECK` constraint for destination: At least one of the fields (location entity or text) must be set:
  `CHECK (to_location_holder_id IS NOT NULL OR to_location_custom_text IS NOT NULL)`

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Unique identifier of the location change record |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id)` | Organization association |
| `serialized_item_id` | `UUID / TEXT` | `NOT NULL, REFERENCES serialized_items(id)` | The item that was moved |
| `admin_holder_id` | `UUID / TEXT` | `NOT NULL, REFERENCES custody_holders(id)` | User/admin who updated the location |
| `from_location_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id)` | Previous entity location |
| `from_location_custom_text` | `TEXT` | `NULLABLE` | Previous free-text location |
| `to_location_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id)` | New entity location (if a registered warehouse/room was selected) |
| `to_location_custom_text` | `TEXT` | `NULLABLE` | New free-text location |
| `notes` | `TEXT` | `NULLABLE` | Reason for move / additional notes |
| `recorded_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Exact timestamp of the location change |

*Indexes:*
* `CREATE INDEX idx_location_logs_item ON serialized_item_location_logs(serialized_item_id);`
* `CREATE INDEX idx_location_logs_org_date ON serialized_item_location_logs(organization_id, recorded_at);`

---

## 8. Field Audits & Scan Checklists (Field Audits & Item Records)

The audit model is based on a **Pre-populated Checklist** generated when the session opens:
1. When opening an audit session (for the entire organization or scoped to a department/holders), the system generates records in `audit_item_records` for all serialized items required for inspection in this session, with an initial status of `0` (Pending scan).
2. At any given moment, audit progress is calculated directly: `(Scanned / Total records in session) * 100`.
3. When a user scans an item:
   * If the item exists in the session: the record is updated to status `1` (Verified) or `2` (Custody mismatch), along with the scanner's ID and scan timestamp.
   * If an item is scanned that was not defined in the original checklist: a row is created with status `3` (Unexpected item scanned).

### `audit_sessions` (Audit Sessions)

*Audit Status Mapping (`status`):*
* `0` = Open for scanning (`OPEN`)
* `1` = Closed (`CLOSED`)
* `2` = Archived (`ARCHIVED`)

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Audit session identifier |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id)` | Organization association |
| `title` | `TEXT` | `NOT NULL` | Audit title (e.g., "Quarterly Equipment Inspection - Operations Dept") |
| `initiated_by_holder_id` | `UUID / TEXT` | `NOT NULL, REFERENCES custody_holders(id)` | Admin who opened the audit |
| `status` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 0` | Audit status |
| `opened_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Opening timestamp |
| `closed_at` | `TIMESTAMPTZ / TEXT` | `NULLABLE` | Closing timestamp |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Audit status/title update timestamp |

*Indexes:*
* `CREATE INDEX idx_audit_sessions_org ON audit_sessions(organization_id);`

### `audit_item_records` (Scan Tracking Records for Each Item in Session)

*Scan Status Mapping (`scan_status`):*
* `0` = Pending scan (`PENDING`) - created when opening the audit, not yet scanned
* `1` = Verified (`VERIFIED`) - scanned by the expected registered custodian
* `2` = Custody mismatch (`MISMATCH`) - scanned by someone other than the registered custodian
* `3` = Unexpected item (`UNEXPECTED`) - item not designated for scanning in this session that was actually scanned

| Field | Type | Constraints / Default | Description |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | Unique tracking row identifier |
| `audit_session_id` | `UUID / TEXT` | `NOT NULL, REFERENCES audit_sessions(id) ON DELETE CASCADE` | Association to specific audit session |
| `serialized_item_id` | `UUID / TEXT` | `NOT NULL, REFERENCES serialized_items(id)` | Audited serialized item |
| `expected_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id)` | Registered holder in system when session opened |
| `scan_status` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 0` | Current tracking status (`0`=Pending, `1`=Verified, `2`=Mismatch, `3`=Unexpected) |
| `scanned_by_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id)` | Person who actually performed the scan |
| `scanned_at` | `TIMESTAMPTZ / TEXT` | `NULLABLE` | Exact timestamp when item was physically scanned |
| `resolved_by_admin_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id)` | Admin who approved/resolved a discrepancy (if any) |
| `resolution_notes` | `TEXT` | `NULLABLE` | Reasoning or action taken following a discrepancy |
| `created_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Record creation timestamp (audit opening) |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | Last update timestamp (scan / discrepancy resolution) |

*Indexes & Constraints:*
* `CREATE UNIQUE INDEX idx_audit_item_session_item ON audit_item_records(audit_session_id, serialized_item_id);`
* `CREATE INDEX idx_audit_item_session_status ON audit_item_records(audit_session_id, scan_status);`
* `CREATE INDEX idx_audit_item_holder ON audit_item_records(scanned_by_holder_id);`
