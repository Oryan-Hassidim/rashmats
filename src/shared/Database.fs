namespace Rashmatz.Shared

open System
open System.Data
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Donald

/// Summary returned when initializing and seeding the SQLite database (`--seed-db` or `POST /dev/seed`).
type DatabaseSeedSummary =
    { DatabaseCreatedOrVerified: bool
      SeededNewData: bool
      OrganizationId: string
      OrganizationSlug: string
      AdminEmail: string
      AdminHolderId: string
      MemberEmail: string
      MemberHolderId: string
      WarehouseHolderId: string
      TemplateId: string }

/// High-Concurrency SQLite Manager:
/// 1. Automatically creates the SQLite `.db` file and applies the full schema (`CREATE TABLE IF NOT EXISTS ...`) on startup.
/// 2. Configures `PRAGMA journal_mode = WAL`, `PRAGMA synchronous = NORMAL`, `PRAGMA busy_timeout = 5000`,
///    and `PRAGMA foreign_keys = ON` on every connection.
/// 3. Readers run completely concurrently without blocking on WAL snapshots.
/// 4. Writers execute through `ExecuteWriteTransactionAsync` which acquires an in-memory asynchronous
///    FIFO gate (`SemaphoreSlim(1, 1)`) and opens a transaction, eliminating `SQLITE_BUSY` deadlocks
///    during high-concurrency bursts (such as 50 field personnel scanning barcodes simultaneously in an audit).
type IDbContext =
    abstract member ConnectionString: string
    abstract member OpenReadConnection: unit -> SqliteConnection
    abstract member ExecuteWriteTransactionAsync<'T> :
        (SqliteConnection -> SqliteTransaction -> Task<Result<'T, ApiError>>) -> Task<Result<'T, ApiError>>

module Database =

    let schemaSql =
        """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA foreign_keys = ON;
        PRAGMA busy_timeout = 5000;

        CREATE TABLE IF NOT EXISTS users (
            id TEXT PRIMARY KEY,
            google_id TEXT UNIQUE NOT NULL,
            email TEXT UNIQUE NOT NULL,
            full_name TEXT NOT NULL,
            avatar_url TEXT NULL,
            is_super_admin INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_users_google_id ON users(google_id);
        CREATE UNIQUE INDEX IF NOT EXISTS idx_users_email ON users(email);

        CREATE TABLE IF NOT EXISTS organizations (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            slug TEXT UNIQUE NOT NULL,
            is_active INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_organizations_slug ON organizations(slug);

        CREATE TABLE IF NOT EXISTS departments (
            id TEXT PRIMARY KEY,
            organization_id TEXT NOT NULL REFERENCES organizations(id),
            name TEXT NOT NULL,
            code TEXT NULL,
            created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_departments_org_name ON departments(organization_id, name);

        CREATE TABLE IF NOT EXISTS custody_holders (
            id TEXT PRIMARY KEY,
            organization_id TEXT NOT NULL REFERENCES organizations(id),
            department_id TEXT NULL REFERENCES departments(id),
            user_id TEXT NULL REFERENCES users(id),
            holder_type INTEGER NOT NULL DEFAULT 1,
            name TEXT NOT NULL,
            internal_identifier TEXT NULL,
            invited_email TEXT NULL,
            role INTEGER NOT NULL DEFAULT 0,
            attendance_status INTEGER NOT NULL DEFAULT 0,
            phone_number TEXT NULL,
            is_active INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_custody_holders_org_internal
            ON custody_holders(organization_id, internal_identifier)
            WHERE internal_identifier IS NOT NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS idx_custody_holders_org_user
            ON custody_holders(organization_id, user_id)
            WHERE user_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_custody_holders_org_email
            ON custody_holders(organization_id, invited_email);
        CREATE INDEX IF NOT EXISTS idx_custody_holders_department
            ON custody_holders(department_id);

        CREATE TABLE IF NOT EXISTS certifications (
            id TEXT PRIMARY KEY,
            organization_id TEXT NOT NULL REFERENCES organizations(id),
            name TEXT NOT NULL,
            description TEXT NULL,
            validity_months INTEGER NULL,
            created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_certifications_org_name ON certifications(organization_id, name);

        CREATE TABLE IF NOT EXISTS holder_certifications (
            id TEXT PRIMARY KEY,
            holder_id TEXT NOT NULL REFERENCES custody_holders(id),
            certification_id TEXT NOT NULL REFERENCES certifications(id),
            acquired_at TEXT NOT NULL,
            expires_at TEXT NULL,
            certificate_number TEXT NULL,
            notes TEXT NULL,
            updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
        CREATE INDEX IF NOT EXISTS idx_holder_cert_holder ON holder_certifications(holder_id);
        CREATE INDEX IF NOT EXISTS idx_holder_cert_cert ON holder_certifications(certification_id);

        CREATE TABLE IF NOT EXISTS item_categories (
            id TEXT PRIMARY KEY,
            organization_id TEXT NOT NULL REFERENCES organizations(id),
            name TEXT NOT NULL,
            description TEXT NULL,
            created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_item_categories_org_name ON item_categories(organization_id, name);

        CREATE TABLE IF NOT EXISTS item_types (
            id TEXT PRIMARY KEY,
            organization_id TEXT NOT NULL REFERENCES organizations(id),
            category_id TEXT NULL REFERENCES item_categories(id),
            name TEXT NOT NULL,
            tracking_type INTEGER NOT NULL DEFAULT 1,
            unit_of_measure TEXT NOT NULL DEFAULT 'unit',
            description TEXT NULL,
            created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
        CREATE INDEX IF NOT EXISTS idx_item_types_org ON item_types(organization_id);
        CREATE INDEX IF NOT EXISTS idx_item_types_category ON item_types(category_id);

        CREATE TABLE IF NOT EXISTS serialized_items (
            id TEXT PRIMARY KEY,
            organization_id TEXT NOT NULL REFERENCES organizations(id),
            item_type_id TEXT NOT NULL REFERENCES item_types(id),
            serial_number TEXT NULL,
            barcode TEXT NOT NULL,
            current_holder_id TEXT NULL REFERENCES custody_holders(id),
            location_holder_id TEXT NULL REFERENCES custody_holders(id),
            location_custom_text TEXT NULL,
            status INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_serialized_org_barcode
            ON serialized_items(organization_id, barcode);
        CREATE UNIQUE INDEX IF NOT EXISTS idx_serialized_org_serial
            ON serialized_items(organization_id, serial_number)
            WHERE serial_number IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_serialized_current_holder ON serialized_items(current_holder_id);
        CREATE INDEX IF NOT EXISTS idx_serialized_location_holder ON serialized_items(location_holder_id);
        CREATE INDEX IF NOT EXISTS idx_serialized_item_type ON serialized_items(item_type_id);

        CREATE TABLE IF NOT EXISTS inventory_stocks (
            id TEXT PRIMARY KEY,
            organization_id TEXT NOT NULL REFERENCES organizations(id),
            item_type_id TEXT NOT NULL REFERENCES item_types(id),
            holder_id TEXT NOT NULL REFERENCES custody_holders(id),
            quantity INTEGER NOT NULL DEFAULT 0,
            updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            CHECK (quantity >= 0)
        );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_inventory_stocks_holder_item
            ON inventory_stocks(holder_id, item_type_id);
        CREATE INDEX IF NOT EXISTS idx_inventory_stocks_org ON inventory_stocks(organization_id);

        CREATE TABLE IF NOT EXISTS transfer_templates (
            id TEXT PRIMARY KEY,
            organization_id TEXT NOT NULL REFERENCES organizations(id),
            name TEXT NOT NULL,
            description TEXT NULL,
            default_source_holder_id TEXT NULL REFERENCES custody_holders(id),
            default_dest_holder_id TEXT NULL REFERENCES custody_holders(id),
            is_active INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_transfer_templates_org_name
            ON transfer_templates(organization_id, name);

        CREATE TABLE IF NOT EXISTS transfer_template_lines (
            template_id TEXT NOT NULL REFERENCES transfer_templates(id) ON DELETE CASCADE,
            line_number INTEGER NOT NULL,
            item_type_id TEXT NOT NULL REFERENCES item_types(id),
            quantity INTEGER NOT NULL DEFAULT 1,
            PRIMARY KEY (template_id, line_number),
            CHECK (quantity > 0)
        );
        CREATE INDEX IF NOT EXISTS idx_template_lines_item_type ON transfer_template_lines(item_type_id);

        CREATE TABLE IF NOT EXISTS custody_transfers (
            id TEXT PRIMARY KEY,
            organization_id TEXT NOT NULL REFERENCES organizations(id),
            transfer_template_id TEXT NULL REFERENCES transfer_templates(id),
            transfer_number TEXT NOT NULL,
            admin_holder_id TEXT NOT NULL REFERENCES custody_holders(id),
            source_holder_id TEXT NULL REFERENCES custody_holders(id),
            dest_holder_id TEXT NULL REFERENCES custody_holders(id),
            notes TEXT NULL,
            transferred_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            CHECK (
                (source_holder_id IS NOT NULL AND dest_holder_id IS NOT NULL)
                OR (notes IS NOT NULL AND TRIM(notes) != '')
            )
        );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_transfers_org_number
            ON custody_transfers(organization_id, transfer_number);
        CREATE INDEX IF NOT EXISTS idx_transfers_source ON custody_transfers(source_holder_id);
        CREATE INDEX IF NOT EXISTS idx_transfers_dest ON custody_transfers(dest_holder_id);
        CREATE INDEX IF NOT EXISTS idx_transfers_org_date
            ON custody_transfers(organization_id, transferred_at DESC);

        CREATE TABLE IF NOT EXISTS custody_transfer_lines (
            transfer_id TEXT NOT NULL REFERENCES custody_transfers(id) ON DELETE CASCADE,
            line_number INTEGER NOT NULL,
            serialized_item_id TEXT NULL REFERENCES serialized_items(id),
            item_type_id TEXT NULL REFERENCES item_types(id),
            quantity INTEGER NOT NULL DEFAULT 1,
            PRIMARY KEY (transfer_id, line_number),
            CHECK (
                (serialized_item_id IS NOT NULL AND item_type_id IS NULL AND quantity = 1)
                OR (serialized_item_id IS NULL AND item_type_id IS NOT NULL AND quantity > 0)
            )
        );
        CREATE INDEX IF NOT EXISTS idx_transfer_lines_serialized_item
            ON custody_transfer_lines(serialized_item_id);

        CREATE TABLE IF NOT EXISTS serialized_item_location_logs (
            id TEXT PRIMARY KEY,
            organization_id TEXT NOT NULL REFERENCES organizations(id),
            serialized_item_id TEXT NOT NULL REFERENCES serialized_items(id),
            admin_holder_id TEXT NOT NULL REFERENCES custody_holders(id),
            from_location_holder_id TEXT NULL REFERENCES custody_holders(id),
            from_location_custom_text TEXT NULL,
            to_location_holder_id TEXT NULL REFERENCES custody_holders(id),
            to_location_custom_text TEXT NULL,
            notes TEXT NULL,
            recorded_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            CHECK (to_location_holder_id IS NOT NULL OR (to_location_custom_text IS NOT NULL AND TRIM(to_location_custom_text) != ''))
        );
        CREATE INDEX IF NOT EXISTS idx_location_logs_item
            ON serialized_item_location_logs(serialized_item_id);
        CREATE INDEX IF NOT EXISTS idx_location_logs_org_date
            ON serialized_item_location_logs(organization_id, recorded_at);

        CREATE TABLE IF NOT EXISTS audit_sessions (
            id TEXT PRIMARY KEY,
            organization_id TEXT NOT NULL REFERENCES organizations(id),
            title TEXT NOT NULL,
            initiated_by_holder_id TEXT NOT NULL REFERENCES custody_holders(id),
            status INTEGER NOT NULL DEFAULT 0,
            opened_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            closed_at TEXT NULL,
            updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
        CREATE INDEX IF NOT EXISTS idx_audit_sessions_org ON audit_sessions(organization_id);

        CREATE TABLE IF NOT EXISTS audit_item_records (
            id TEXT PRIMARY KEY,
            audit_session_id TEXT NOT NULL REFERENCES audit_sessions(id) ON DELETE CASCADE,
            serialized_item_id TEXT NOT NULL REFERENCES serialized_items(id),
            expected_holder_id TEXT NULL REFERENCES custody_holders(id),
            scan_status INTEGER NOT NULL DEFAULT 0,
            scanned_by_holder_id TEXT NULL REFERENCES custody_holders(id),
            scanned_at TEXT NULL,
            resolved_by_admin_id TEXT NULL REFERENCES custody_holders(id),
            resolution_notes TEXT NULL,
            created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_audit_item_session_item
            ON audit_item_records(audit_session_id, serialized_item_id);
        CREATE INDEX IF NOT EXISTS idx_audit_item_session_status
            ON audit_item_records(audit_session_id, scan_status);
        CREATE INDEX IF NOT EXISTS idx_audit_item_holder
            ON audit_item_records(scanned_by_holder_id);

        -- Supporting tables for 30-day HttpOnly Sessions and Transfer Idempotency-Key
        CREATE TABLE IF NOT EXISTS user_sessions (
            token_hash TEXT PRIMARY KEY,
            user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            active_org_id TEXT NULL REFERENCES organizations(id),
            expires_at TEXT NOT NULL,
            created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
        CREATE INDEX IF NOT EXISTS idx_user_sessions_user ON user_sessions(user_id);

        CREATE TABLE IF NOT EXISTS idempotency_keys (
            organization_id TEXT NOT NULL REFERENCES organizations(id),
            idempotency_key TEXT NOT NULL,
            endpoint_name TEXT NOT NULL,
            response_json TEXT NOT NULL,
            created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            PRIMARY KEY (organization_id, idempotency_key, endpoint_name)
        );
        """

    let private configureConnection (conn: SqliteConnection) =
        if conn.State <> ConnectionState.Open then
            conn.Open()

        conn
        |> Db.newCommand
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            """
        |> Db.exec

    type SqliteDbContext(connectionString: string) =
        // FIFO semaphore serializing write transactions in-process so concurrent audit scans / transfers
        // queue smoothly in microseconds without SQLITE_BUSY lock upgrade failures.
        let writeGate = new SemaphoreSlim(1, 1)

        do
            use conn = new SqliteConnection(connectionString)
            configureConnection conn
            conn |> Db.newCommand schemaSql |> Db.exec

        interface IDbContext with
            member _.ConnectionString = connectionString

            member _.OpenReadConnection() =
                let conn = new SqliteConnection(connectionString)
                configureConnection conn
                conn

            member _.ExecuteWriteTransactionAsync<'T>
                (work: SqliteConnection -> SqliteTransaction -> Task<Result<'T, ApiError>>)
                : Task<Result<'T, ApiError>> =
                task {
                    do! writeGate.WaitAsync()

                    try
                        use conn = new SqliteConnection(connectionString)
                        configureConnection conn
                        use tx = conn.BeginTransaction(IsolationLevel.Serializable)

                        try
                            let! outcome = work conn tx

                            match outcome with
                            | Ok value ->
                                tx.Commit()
                                return Ok value
                            | Error apiErr ->
                                tx.Rollback()
                                return Error apiErr
                        with _ ->
                            try
                                tx.Rollback()
                            with _ ->
                                ()

                            return Error(ApiError.single ApiErrorCode.InternalError)
                    finally
                        writeGate.Release() |> ignore
                }

    /// Idempotently seeds realistic initial development data into the SQLite database if it has no organizations yet.
    /// Fixed deterministic UUIDs are used for the default dev seed so API tests and curl/Postman scripts
    /// can reference predictable IDs right away.
    let ensureDevelopmentSeed (db: IDbContext) : DatabaseSeedSummary =
        use conn = db.OpenReadConnection()

        let existingOrgOpt =
            conn
            |> Db.newCommand "SELECT id, slug FROM organizations ORDER BY created_at ASC LIMIT 1"
            |> Db.querySingle (fun rd -> rd.ReadString "id", rd.ReadString "slug")

        let defaultOrgId = "11111111-1111-4111-8111-111111111111"
        let defaultDeptId = "22222222-2222-4222-8222-222222222222"
        let defaultAdminUserId = "33333333-3333-4333-8333-333333333331"
        let defaultMemberUserId = "33333333-3333-4333-8333-333333333332"
        let defaultAdminHolderId = "44444444-4444-4444-8444-444444444441"
        let defaultMemberHolderId = "44444444-4444-4444-8444-444444444442"
        let defaultWarehouseHolderId = "44444444-4444-4444-8444-444444444443"
        let defaultCategoryId = "55555555-5555-4555-8555-555555555551"
        let defaultRadioTypeId = "66666666-6666-4666-8666-666666666661"
        let defaultBatteryTypeId = "66666666-6666-4666-8666-666666666662"
        let defaultRadio1Id = "77777777-7777-4777-8777-777777777771"
        let defaultRadio2Id = "77777777-7777-4777-8777-777777777772"
        let defaultStockId = "88888888-8888-4888-8888-888888888881"
        let defaultTemplateId = "99999999-9999-4999-8999-999999999991"

        match existingOrgOpt with
        | Some(orgId, slug) ->
            { DatabaseCreatedOrVerified = true
              SeededNewData = false
              OrganizationId = orgId
              OrganizationSlug = slug
              AdminEmail = "admin@rashmatz.org"
              AdminHolderId = defaultAdminHolderId
              MemberEmail = "soldier@rashmatz.org"
              MemberHolderId = defaultMemberHolderId
              WarehouseHolderId = defaultWarehouseHolderId
              TemplateId = defaultTemplateId }
        | None ->
            let nowIso = DateTimeOffset.UtcNow.ToString("O")

            conn
            |> Db.newCommand
                """
                INSERT INTO organizations (id, name, slug, is_active, created_at, updated_at)
                VALUES (@org_id, 'Unit 8200 Logistics', 'unit-8200', 1, @now, @now);

                INSERT INTO departments (id, organization_id, name, code, created_at, updated_at)
                VALUES (@dept_id, @org_id, 'Field Communications', 'COM', @now, @now);

                INSERT INTO users (id, google_id, email, full_name, is_super_admin, created_at, updated_at)
                VALUES
                    (@admin_user_id, 'google-admin-1', 'admin@rashmatz.org', 'Commander Admin', 1, @now, @now),
                    (@member_user_id, 'google-member-1', 'soldier@rashmatz.org', 'Field Operator', 0, @now, @now);

                INSERT INTO custody_holders (
                    id, organization_id, department_id, user_id, holder_type,
                    name, internal_identifier, invited_email, role, attendance_status, phone_number, is_active, created_at, updated_at
                )
                VALUES
                    (@admin_holder_id, @org_id, @dept_id, @admin_user_id, 1, 'Commander Admin', 'ADM-01', 'admin@rashmatz.org', 2, 1, '+972501112233', 1, @now, @now),
                    (@member_holder_id, @org_id, @dept_id, @member_user_id, 1, 'Field Operator', 'OP-102', 'soldier@rashmatz.org', 1, 1, '+972504445566', 1, @now, @now),
                    (@warehouse_holder_id, @org_id, @dept_id, NULL, 0, 'Main Armory Vault', 'WH-01', NULL, 0, 0, NULL, 1, @now, @now);

                INSERT INTO item_categories (id, organization_id, name, description, created_at, updated_at)
                VALUES (@cat_id, @org_id, 'Communications', 'Tactical radios and accessories', @now, @now);

                INSERT INTO item_types (id, organization_id, category_id, name, tracking_type, unit_of_measure, description, created_at, updated_at)
                VALUES
                    (@radio_type_id, @org_id, @cat_id, 'Tactical Radio PRC-152', 1, 'unit', 'Encrypted VHF/UHF handheld', @now, @now),
                    (@battery_type_id, @org_id, @cat_id, 'AA Battery Pack', 0, 'pack', '8x AA lithium pack', @now, @now);

                INSERT INTO serialized_items (
                    id, organization_id, item_type_id, serial_number, barcode,
                    current_holder_id, location_holder_id, location_custom_text, status, created_at, updated_at
                )
                VALUES
                    (@radio1_id, @org_id, @radio_type_id, 'SN-PRC-001', 'BC-PRC-001', @warehouse_holder_id, @warehouse_holder_id, 'Shelf A1', 0, @now, @now),
                    (@radio2_id, @org_id, @radio_type_id, 'SN-PRC-002', 'BC-PRC-002', @admin_holder_id, @member_holder_id, 'Backpack Pouch', 0, @now, @now);

                INSERT INTO inventory_stocks (id, organization_id, item_type_id, holder_id, quantity, updated_at)
                VALUES (@stock_id, @org_id, @battery_type_id, @warehouse_holder_id, 50, @now);

                INSERT INTO transfer_templates (
                    id, organization_id, name, description, default_source_holder_id, default_dest_holder_id, is_active, created_at, updated_at
                )
                VALUES (
                    @tpl_id, @org_id, 'Standard Comms Kit', '1 Radio + 4 Battery Packs', @warehouse_holder_id, @member_holder_id, 1, @now, @now
                );

                INSERT INTO transfer_template_lines (template_id, line_number, item_type_id, quantity)
                VALUES
                    (@tpl_id, 1, @radio_type_id, 1),
                    (@tpl_id, 2, @battery_type_id, 4);
                """
            |> Db.setParams
                [ "org_id", SqlType.String defaultOrgId
                  "dept_id", SqlType.String defaultDeptId
                  "admin_user_id", SqlType.String defaultAdminUserId
                  "member_user_id", SqlType.String defaultMemberUserId
                  "admin_holder_id", SqlType.String defaultAdminHolderId
                  "member_holder_id", SqlType.String defaultMemberHolderId
                  "warehouse_holder_id", SqlType.String defaultWarehouseHolderId
                  "cat_id", SqlType.String defaultCategoryId
                  "radio_type_id", SqlType.String defaultRadioTypeId
                  "battery_type_id", SqlType.String defaultBatteryTypeId
                  "radio1_id", SqlType.String defaultRadio1Id
                  "radio2_id", SqlType.String defaultRadio2Id
                  "stock_id", SqlType.String defaultStockId
                  "tpl_id", SqlType.String defaultTemplateId
                  "now", SqlType.String nowIso ]
            |> Db.exec

            { DatabaseCreatedOrVerified = true
              SeededNewData = true
              OrganizationId = defaultOrgId
              OrganizationSlug = "unit-8200"
              AdminEmail = "admin@rashmatz.org"
              AdminHolderId = defaultAdminHolderId
              MemberEmail = "soldier@rashmatz.org"
              MemberHolderId = defaultMemberHolderId
              WarehouseHolderId = defaultWarehouseHolderId
              TemplateId = defaultTemplateId }

    /// Concurrent-safe audit barcode scan helper demonstrating how high-throughput field scans
    /// are serialized safely via the write gate during an active audit session.
    let recordAuditScanConcurrently
        (db: IDbContext)
        (orgId: DomainTypes.OrgId)
        (sessionId: DomainTypes.AuditSessionId)
        (scannerHolderId: DomainTypes.HolderId)
        (barcode: DomainTypes.Barcode)
        : Task<Result<int * string * string option, ApiError>> =
        db.ExecuteWriteTransactionAsync(fun conn tx ->
            task {
                let nowIso = DateTimeOffset.UtcNow.ToString("O")

                let itemOpt =
                    conn
                    |> Db.newCommand
                        """
                        SELECT si.id, air.id AS record_id, air.expected_holder_id
                        FROM serialized_items si
                        LEFT JOIN audit_item_records air
                            ON air.serialized_item_id = si.id
                           AND air.audit_session_id = @session_id
                        WHERE si.organization_id = @org_id
                          AND si.barcode = @barcode
                        """
                    |> Db.setTransaction tx
                    |> Db.setParams
                        [ "session_id", SqlType.String(sessionId.ToDbString())
                          "org_id", SqlType.String(orgId.ToDbString())
                          "barcode", SqlType.String barcode.Value ]
                    |> Db.querySingle (fun rd ->
                        rd.ReadString "id",
                        rd.ReadStringOption "record_id",
                        rd.ReadStringOption "expected_holder_id")

                match itemOpt with
                | None -> return Error(ApiError.single ApiErrorCode.AuditItemOutOfScope)
                | Some(_, None, _) -> return Error(ApiError.single ApiErrorCode.AuditItemOutOfScope)
                | Some(serializedItemId, Some recordId, expectedHolderOpt) ->
                    let scannerStr = scannerHolderId.ToDbString()

                    let newScanStatus =
                        match expectedHolderOpt with
                        | Some expected when String.Equals(expected, scannerStr, StringComparison.OrdinalIgnoreCase) ->
                            int DomainTypes.AuditScanStatus.Verified
                        | _ -> int DomainTypes.AuditScanStatus.Mismatch

                    conn
                    |> Db.newCommand
                        """
                        UPDATE audit_item_records
                        SET scan_status = @scan_status,
                            scanned_by_holder_id = @scanned_by,
                            scanned_at = @now,
                            updated_at = @now
                        WHERE id = @record_id
                        """
                    |> Db.setTransaction tx
                    |> Db.setParams
                        [ "scan_status", SqlType.Int32 newScanStatus
                          "scanned_by", SqlType.String scannerStr
                          "now", SqlType.String nowIso
                          "record_id", SqlType.String recordId ]
                    |> Db.exec

                    return Ok(newScanStatus, serializedItemId, expectedHolderOpt)
            })
