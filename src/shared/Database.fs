namespace Rashmatz.Shared

open System
open System.Data
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Donald

type DevSeedSummary =
    { SeededNewData: bool
      OrganizationId: string
      OrganizationSlug: string
      AdminEmail: string
      AdminHolderId: string
      MemberEmail: string
      MemberHolderId: string
      WarehouseHolderId: string
      TemplateId: string
      Radio1Id: string
      BatteryTypeId: string }

/// High-concurrency SQLite context:
/// - Applies `src/shared/schema.sql` automatically on startup.
/// - Enables WAL + `busy_timeout = 5000` + `foreign_keys = ON`.
/// - Serializes writers through a FIFO `SemaphoreSlim(1,1)` gate to eliminate `SQLITE_BUSY` under parallel audit scans.
type IDbContext =
    abstract OpenRead: unit -> SqliteConnection

    abstract WriteTxAsync<'T> :
        (SqliteConnection -> SqliteTransaction -> Task<Result<'T, ApiError>>) -> Task<Result<'T, ApiError>>

module Database =

    let private openConfigured (connStr: string) =
        let conn = new SqliteConnection(connStr)
        conn.Open()

        conn
        |> Db.newCommand
            "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"
        |> Db.exec

        conn

    type SqliteDbContext(connectionString: string) =
        let writeGate = new SemaphoreSlim(1, 1)

        do
            use conn = openConfigured connectionString

            conn
            |> Db.newCommand (Sql.load "shared/schema.sql")
            |> Db.exec

        interface IDbContext with
            member _.OpenRead() = openConfigured connectionString

            member _.WriteTxAsync<'T> work =
                task {
                    do! writeGate.WaitAsync()

                    try
                        use conn = openConfigured connectionString
                        use tx = conn.BeginTransaction(IsolationLevel.Serializable)

                        try
                            match! work conn tx with
                            | Ok (v: 'T) ->
                                tx.Commit()
                                return Ok v
                            | Error e ->
                                tx.Rollback()
                                return Error e
                        with
                        | _ ->
                            try
                                tx.Rollback()
                            with
                            | _ -> ()

                            return Error(ApiError.single ApiErrorCode.InternalError)
                    finally
                        writeGate.Release() |> ignore
                }

    /// Idempotently seeds deterministic dev data if no organization exists yet.
    let ensureDevSeed (db: IDbContext) : DevSeedSummary =
        let orgId = "11111111-1111-4111-8111-111111111111"
        let deptId = "22222222-2222-4222-8222-222222222222"
        let adminUserId = "33333333-3333-4333-8333-333333333331"
        let memberUserId = "33333333-3333-4333-8333-333333333332"
        let adminHolderId = "44444444-4444-4444-8444-444444444441"
        let memberHolderId = "44444444-4444-4444-8444-444444444442"
        let warehouseHolderId = "44444444-4444-4444-8444-444444444443"
        let catId = "55555555-5555-4555-8555-555555555551"
        let radioTypeId = "66666666-6666-4666-8666-666666666661"
        let batteryTypeId = "66666666-6666-4666-8666-666666666662"
        let radio1Id = "77777777-7777-4777-8777-777777777771"
        let radio2Id = "77777777-7777-4777-8777-777777777772"
        let stockId = "88888888-8888-4888-8888-888888888881"
        let templateId = "99999999-9999-4999-8999-999999999991"

        use conn = db.OpenRead()

        let existing =
            conn
            |> Db.newCommand "SELECT id, slug FROM organizations ORDER BY created_at ASC LIMIT 1"
            |> Db.querySingle (fun rd -> rd.ReadString "id", rd.ReadString "slug")

        match existing with
        | Some (existingOrgId, existingSlug) ->
            { SeededNewData = false
              OrganizationId = existingOrgId
              OrganizationSlug = existingSlug
              AdminEmail = "admin@rashmatz.org"
              AdminHolderId = adminHolderId
              MemberEmail = "soldier@rashmatz.org"
              MemberHolderId = memberHolderId
              WarehouseHolderId = warehouseHolderId
              TemplateId = templateId
              Radio1Id = radio1Id
              BatteryTypeId = batteryTypeId }
        | None ->
            let now = DateTimeOffset.UtcNow.ToString("O")

            conn
            |> Db.newCommand
                """
                INSERT INTO organizations (id, name, slug, is_active, created_at, updated_at)
                VALUES (@org_id, 'Unit 8200 Logistics', 'unit-8200', 1, @now, @now);

                INSERT INTO departments (id, organization_id, name, code, created_at, updated_at)
                VALUES (@dept_id, @org_id, 'Field Communications', 'COM', @now, @now);

                INSERT INTO users (id, google_id, email, full_name, is_super_admin, created_at, updated_at) VALUES
                    (@admin_uid, 'google-admin-1', 'admin@rashmatz.org', 'Commander Admin', 1, @now, @now),
                    (@member_uid, 'google-member-1', 'soldier@rashmatz.org', 'Field Operator', 0, @now, @now);

                INSERT INTO custody_holders (
                    id, organization_id, department_id, user_id, holder_type, name,
                    internal_identifier, invited_email, role, attendance_status, phone_number, is_active, created_at, updated_at
                ) VALUES
                    (@admin_hid, @org_id, @dept_id, @admin_uid, 1, 'Commander Admin', 'ADM-01', 'admin@rashmatz.org', 2, 1, '+972501112233', 1, @now, @now),
                    (@member_hid, @org_id, @dept_id, @member_uid, 1, 'Field Operator', 'OP-102', 'soldier@rashmatz.org', 1, 1, '+972504445566', 1, @now, @now),
                    (@wh_hid, @org_id, @dept_id, NULL, 0, 'Main Armory Vault', 'WH-01', NULL, 0, 0, NULL, 1, @now, @now);

                INSERT INTO item_categories (id, organization_id, name, description, created_at, updated_at)
                VALUES (@cat_id, @org_id, 'Communications', 'Tactical radios and accessories', @now, @now);

                INSERT INTO item_types (id, organization_id, category_id, name, tracking_type, unit_of_measure, description, created_at, updated_at) VALUES
                    (@radio_tid, @org_id, @cat_id, 'Tactical Radio PRC-152', 1, 'unit', 'Encrypted handheld', @now, @now),
                    (@bat_tid, @org_id, @cat_id, 'AA Battery Pack', 0, 'pack', '8x AA lithium pack', @now, @now);

                INSERT INTO serialized_items (
                    id, organization_id, item_type_id, serial_number, barcode,
                    current_holder_id, location_holder_id, location_custom_text, status, created_at, updated_at
                ) VALUES
                    (@radio1, @org_id, @radio_tid, 'SN-PRC-001', 'BC-PRC-001', @wh_hid, @wh_hid, 'Shelf A1', 0, @now, @now),
                    (@radio2, @org_id, @radio_tid, 'SN-PRC-002', 'BC-PRC-002', @admin_hid, @member_hid, 'Backpack Pouch', 0, @now, @now);

                INSERT INTO inventory_stocks (id, organization_id, item_type_id, holder_id, quantity, updated_at)
                VALUES (@stock_id, @org_id, @bat_tid, @wh_hid, 50, @now);

                INSERT INTO transfer_templates (id, organization_id, name, description, default_source_holder_id, default_dest_holder_id, is_active, created_at, updated_at)
                VALUES (@tpl_id, @org_id, 'Standard Comms Kit', '1 Radio + 4 Battery Packs', @wh_hid, @member_hid, 1, @now, @now);

                INSERT INTO transfer_template_lines (template_id, line_number, item_type_id, quantity) VALUES
                    (@tpl_id, 1, @radio_tid, 1),
                    (@tpl_id, 2, @bat_tid, 4);
                """
            |> Db.setParams [ "org_id", SqlType.String orgId
                              "dept_id", SqlType.String deptId
                              "admin_uid", SqlType.String adminUserId
                              "member_uid", SqlType.String memberUserId
                              "admin_hid", SqlType.String adminHolderId
                              "member_hid", SqlType.String memberHolderId
                              "wh_hid", SqlType.String warehouseHolderId
                              "cat_id", SqlType.String catId
                              "radio_tid", SqlType.String radioTypeId
                              "bat_tid", SqlType.String batteryTypeId
                              "radio1", SqlType.String radio1Id
                              "radio2", SqlType.String radio2Id
                              "stock_id", SqlType.String stockId
                              "tpl_id", SqlType.String templateId
                              "now", SqlType.String now ]
            |> Db.exec

            { SeededNewData = true
              OrganizationId = orgId
              OrganizationSlug = "unit-8200"
              AdminEmail = "admin@rashmatz.org"
              AdminHolderId = adminHolderId
              MemberEmail = "soldier@rashmatz.org"
              MemberHolderId = memberHolderId
              WarehouseHolderId = warehouseHolderId
              TemplateId = templateId
              Radio1Id = radio1Id
              BatteryTypeId = batteryTypeId }

    /// Serialized concurrent-safe audit barcode scan write.
    let recordAuditScan
        (db: IDbContext)
        (orgId: OrgId)
        (sessionId: AuditSessionId)
        (scanner: HolderId)
        (barcode: Barcode)
        =
        db.WriteTxAsync (fun conn tx ->
            task {
                let now = DateTimeOffset.UtcNow.ToString("O")

                let found =
                    conn
                    |> Db.newCommand
                        """
                        SELECT si.id, air.id AS record_id, air.expected_holder_id
                        FROM serialized_items si
                        LEFT JOIN audit_item_records air ON air.serialized_item_id = si.id AND air.audit_session_id = @sid
                        WHERE si.organization_id = @org_id AND si.barcode = @barcode
                        """
                    |> Db.setTransaction tx
                    |> Db.setParams [ "sid", SqlType.String sessionId.Str
                                      "org_id", SqlType.String orgId.Str
                                      "barcode", SqlType.String barcode.Value ]
                    |> Db.querySingle (fun rd ->
                        rd.ReadString "id", rd.ReadStringOption "record_id", rd.ReadStringOption "expected_holder_id")

                match found with
                | Some (itemId, Some recordId, expectedOpt) ->
                    let status =
                        if expectedOpt = Some scanner.Str then
                            int AuditScanStatus.Verified
                        else
                            int AuditScanStatus.Mismatch

                    conn
                    |> Db.newCommand
                        "UPDATE audit_item_records SET scan_status=@st, scanned_by_holder_id=@by, scanned_at=@now, updated_at=@now WHERE id=@id"
                    |> Db.setTransaction tx
                    |> Db.setParams [ "st", SqlType.Int32 status
                                      "by", SqlType.String scanner.Str
                                      "now", SqlType.String now
                                      "id", SqlType.String recordId ]
                    |> Db.exec

                    return Ok(status, itemId, expectedOpt)
                | _ -> return Error(ApiError.single ApiErrorCode.AuditItemOutOfScope)
            })
