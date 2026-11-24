namespace Rashmatz.Pages.TransfersNewItems

open System
open System.Text.Json
open Microsoft.Data.Sqlite
open Donald
open FsToolkit.ErrorHandling
open Rashmatz.Shared
open Rashmatz.Pages.TransfersNewItems.Domain

/// Step 6C Database Operations — broken into small, self-documenting, composable Railway functions.
module Db =

    // ── 1. Focused Step 6C Bootstrap ─────────────────────────────────────────
    let loadBootstrap (db: IDbContext) (orgId: OrgId) (srcOpt: HolderId option) (dstOpt: HolderId option) (tplOpt: TemplateId option) =
        use conn = db.OpenRead()
        let org = orgId.Str

        let categories =
            conn |> Db.newCommand "SELECT id, name, description FROM item_categories WHERE organization_id=@org ORDER BY name"
            |> Db.setParams [ "org", SqlType.String org ]
            |> Db.query (fun rd -> ({ Id = rd.ReadString "id"; Name = rd.ReadString "name"; Description = rd.ReadStringOption "description" } : Rashmatz.Pages.MyCustody.Server.CategorySummary))

        let srcSer, srcQuant =
            match srcOpt with
            | None -> [], []
            | Some src ->
                let ser =
                    conn |> Db.newCommand "SELECT si.id, si.item_type_id, it.name AS tn, it.category_id, it.unit_of_measure, si.barcode, si.serial_number, si.status, si.location_holder_id, si.location_custom_text FROM serialized_items si INNER JOIN item_types it ON it.id=si.item_type_id WHERE si.organization_id=@org AND si.current_holder_id=@hid ORDER BY it.name, si.barcode"
                    |> Db.setParams [ "org", SqlType.String org; "hid", SqlType.String src.Str ]
                    |> Db.query (fun rd -> { Id = rd.ReadString "id"; ItemTypeId = rd.ReadString "item_type_id"; ItemTypeName = rd.ReadString "tn"; CategoryId = rd.ReadStringOption "category_id"; UnitOfMeasure = rd.ReadString "unit_of_measure"; Barcode = rd.ReadString "barcode"; SerialNumber = rd.ReadStringOption "serial_number"; Status = rd.ReadInt32 "status"; LocationHolderId = rd.ReadStringOption "location_holder_id"; LocationCustomText = rd.ReadStringOption "location_custom_text" })
                let quant =
                    conn |> Db.newCommand "SELECT st.item_type_id, it.name AS tn, it.category_id, it.unit_of_measure, st.quantity FROM inventory_stocks st INNER JOIN item_types it ON it.id=st.item_type_id WHERE st.organization_id=@org AND st.holder_id=@hid AND st.quantity > 0 ORDER BY it.name"
                    |> Db.setParams [ "org", SqlType.String org; "hid", SqlType.String src.Str ]
                    |> Db.query (fun rd -> { ItemTypeId = rd.ReadString "item_type_id"; ItemTypeName = rd.ReadString "tn"; CategoryId = rd.ReadStringOption "category_id"; UnitOfMeasure = rd.ReadString "unit_of_measure"; Quantity = rd.ReadInt32 "quantity" })
                ser, quant

        let destBalances =
            match dstOpt with
            | None -> []
            | Some dst ->
                conn |> Db.newCommand (Sql.load "pages/transfers-new-items/dest_balances.sql")
                |> Db.setParams [ "org_id", SqlType.String org; "holder_id", SqlType.String dst.Str ]
                |> Db.query (fun rd -> { ItemTypeId = rd.ReadString "item_type_id"; TrackingType = rd.ReadInt32 "tracking_type"; CurrentQuantity = rd.ReadInt32 "current_quantity" })

        let templateResult =
            match tplOpt with
            | None -> Ok None
            | Some tpl ->
                conn |> Db.newCommand "SELECT id, name, description, default_source_holder_id, default_dest_holder_id FROM transfer_templates WHERE id=@id AND organization_id=@org AND is_active=1"
                |> Db.setParams [ "id", SqlType.String tpl.Str; "org", SqlType.String org ]
                |> Db.querySingle (fun rd -> rd.ReadString "id", rd.ReadString "name", rd.ReadStringOption "description", rd.ReadStringOption "default_source_holder_id", rd.ReadStringOption "default_dest_holder_id")
                |> function
                    | None -> Error(ApiError.withTarget ApiErrorCode.TemplateNotFound tpl.Str)
                    | Some(id, name, desc, defSrc, defDst) ->
                        let lines =
                            conn |> Db.newCommand "SELECT tl.line_number, tl.item_type_id, it.name AS tn, it.tracking_type, tl.quantity FROM transfer_template_lines tl INNER JOIN item_types it ON it.id=tl.item_type_id WHERE tl.template_id=@id ORDER BY tl.line_number"
                            |> Db.setParams [ "id", SqlType.String tpl.Str ]
                            |> Db.query (fun rd -> { LineNumber = rd.ReadInt32 "line_number"; ItemTypeId = rd.ReadString "item_type_id"; ItemTypeName = rd.ReadString "tn"; TrackingType = rd.ReadInt32 "tracking_type"; Quantity = rd.ReadInt32 "quantity" })
                        Ok(Some { Id = id; Name = name; Description = desc; DefaultSourceHolderId = defSrc; DefaultDestHolderId = defDst; Lines = lines })

        let intakeCatalog =
            match srcOpt with
            | Some _ -> None
            | None ->
                conn |> Db.newCommand "SELECT it.id, it.category_id, ic.name AS cn, it.name, it.tracking_type, it.unit_of_measure, it.description FROM item_types it LEFT JOIN item_categories ic ON ic.id=it.category_id WHERE it.organization_id=@org ORDER BY it.name"
                |> Db.setParams [ "org", SqlType.String org ]
                |> Db.query (fun rd -> { Id = rd.ReadString "id"; CategoryId = rd.ReadStringOption "category_id"; CategoryName = rd.ReadStringOption "cn"; Name = rd.ReadString "name"; TrackingType = rd.ReadInt32 "tracking_type"; UnitOfMeasure = rd.ReadString "unit_of_measure"; Description = rd.ReadStringOption "description" })
                |> Some

        templateResult |> Result.map (fun tpl ->
            { SourceHolderId = srcOpt |> Option.map _.Str; DestHolderId = dstOpt |> Option.map _.Str
              Categories = categories; SourceSerializedItems = srcSer; SourceQuantitativeBalances = srcQuant
              DestBalances = destBalances; SelectedTemplate = tpl; IntakeCatalogItemTypes = intakeCatalog })

    // ── 2. Check Unassigned Barcode / Serial ─────────────────────────────────
    let checkUnassigned (db: IDbContext) (orgId: OrgId) (req: CheckUnassignedQuery) =
        use conn = db.OpenRead()
        let findBy col valueOpt =
            valueOpt |> Option.bind (fun v ->
                conn |> Db.newCommand $"SELECT id FROM serialized_items WHERE organization_id=@org AND {col}=@v"
                |> Db.setParams [ "org", SqlType.String orgId.Str; "v", SqlType.String v ]
                |> Db.querySingle (fun rd -> rd.ReadString "id"))

        let bcConflict = findBy "barcode" (trimOpt req.Barcode)
        let snConflict = findBy "serial_number" (trimOpt req.SerialNumber)
        Ok { BarcodeAvailable = bcConflict.IsNone; SerialNumberAvailable = snConflict.IsNone; ConflictingItemId = Option.orElse snConflict bcConflict }

    // ── 3. Create Catalog Item Type ──────────────────────────────────────────
    let createItemType (db: IDbContext) (orgId: OrgId) (cmd: ValidatedCreateItemType) =
        db.WriteTxAsync(fun conn tx ->
            task {
                let org, now = orgId.Str, DateTimeOffset.UtcNow.ToString("O")
                let catResult =
                    match cmd.Category with
                    | Uncategorized -> Ok(None, None)
                    | ExistingCategory cid ->
                        conn |> Db.newCommand "SELECT name FROM item_categories WHERE id=@id AND organization_id=@org"
                        |> Db.setTransaction tx |> Db.setParams [ "id", SqlType.String cid.Str; "org", SqlType.String org ]
                        |> Db.querySingle (fun rd -> rd.ReadString "name")
                        |> function Some n -> Ok(Some cid.Str, Some n) | None -> Error(ApiError.withTarget ApiErrorCode.CategoryNotFound cid.Str)
                    | NewCategory cname ->
                        match conn |> Db.newCommand "SELECT id, name FROM item_categories WHERE organization_id=@org AND LOWER(name)=LOWER(@n)"
                              |> Db.setTransaction tx |> Db.setParams [ "org", SqlType.String org; "n", SqlType.String cname.Value ]
                              |> Db.querySingle (fun rd -> rd.ReadString "id", rd.ReadString "name") with
                        | Some(id, n) -> Ok(Some id, Some n)
                        | None ->
                            let cid = Guid.NewGuid().ToString("D")
                            conn |> Db.newCommand "INSERT INTO item_categories (id, organization_id, name, created_at, updated_at) VALUES (@id, @org, @n, @now, @now)"
                            |> Db.setTransaction tx |> Db.setParams [ "id", SqlType.String cid; "org", SqlType.String org; "n", SqlType.String cname.Value; "now", SqlType.String now ] |> Db.exec
                            Ok(Some cid, Some cname.Value)

                return catResult |> Result.map (fun (catId, catName) ->
                    let id = Guid.NewGuid().ToString("D")
                    conn |> Db.newCommand "INSERT INTO item_types (id, organization_id, category_id, name, tracking_type, unit_of_measure, description, created_at, updated_at) VALUES (@id, @org, @cid, @n, @tt, @uom, @d, @now, @now)"
                    |> Db.setTransaction tx |> Db.setParams [ "id", SqlType.String id; "org", SqlType.String org; "cid", Sql.strOpt catId; "n", SqlType.String cmd.Name.Value; "tt", SqlType.Int32(int cmd.TrackingType); "uom", SqlType.String cmd.UnitOfMeasure; "d", Sql.strOpt cmd.Description; "now", SqlType.String now ] |> Db.exec
                    { Id = id; CategoryId = catId; CategoryName = catName; Name = cmd.Name.Value; TrackingType = int cmd.TrackingType; UnitOfMeasure = cmd.UnitOfMeasure; Description = cmd.Description })
            })

    // ── 4. Atomic Commit Transfer (Small Composable Railway Steps) ───────────
    let private findCachedIdempotency (conn: SqliteConnection) tx org keyOpt =
        keyOpt |> Option.bind (fun key ->
            conn |> Db.newCommand "SELECT response_json FROM idempotency_keys WHERE organization_id=@org AND idempotency_key=@k AND endpoint_name='POST /transfers'"
            |> Db.setTransaction tx |> Db.setParams [ "org", SqlType.String org; "k", SqlType.String key ]
            |> Db.querySingle (fun rd -> rd.ReadString "response_json")
            |> Option.bind (fun json ->
                match JsonSerializer.Deserialize<CommitTransferResponse>(json, Http.snakeCaseJsonOptions) with
                | null -> None
                | dto -> Some dto))

    let private verifyHolderActive (conn: SqliteConnection) tx org (holderOpt: HolderId option) =
        match holderOpt with
        | None -> Ok()
        | Some h ->
            match conn |> Db.newCommand "SELECT is_active FROM custody_holders WHERE id=@id AND organization_id=@org"
                  |> Db.setTransaction tx |> Db.setParams [ "id", SqlType.String h.Str; "org", SqlType.String org ]
                  |> Db.querySingle (fun rd -> rd.ReadInt32 "is_active" = 1) with
            | None -> Error(ApiError.withTarget ApiErrorCode.HolderNotFound h.Str)
            | Some false -> Error(ApiError.withTarget ApiErrorCode.HolderInactive h.Str)
            | Some true -> Ok()

    let private verifyExistingSerialized (conn: SqliteConnection) tx org (expectedSrc: HolderId option) (ids: SerializedItemId list) =
        ids |> List.traverseResultM (fun sid ->
            match conn |> Db.newCommand "SELECT current_holder_id FROM serialized_items WHERE id=@id AND organization_id=@org"
                  |> Db.setTransaction tx |> Db.setParams [ "id", SqlType.String sid.Str; "org", SqlType.String org ]
                  |> Db.querySingle (fun rd -> rd.ReadStringOption "current_holder_id"), expectedSrc with
            | None, _ -> Error(ApiError.withTarget ApiErrorCode.SerializedItemNotFound sid.Str)
            | Some curr, Some src when curr = Some src.Str -> Ok sid.Str
            | Some _, Some _ -> Error(ApiError.withTarget ApiErrorCode.ItemNotOwnedBySourceHolder sid.Str)
            | Some _, None -> Error(ApiError.withTarget ApiErrorCode.ValidationFailed sid.Str))

    let private insertIntakeSerialized (conn: SqliteConnection) tx org (destOpt: HolderId option) now (specs: IntakeSpec list) =
        specs |> List.traverseResultM (fun s ->
            result {
                let! tracking =
                    conn |> Db.newCommand "SELECT tracking_type FROM item_types WHERE id=@id AND organization_id=@org"
                    |> Db.setTransaction tx |> Db.setParams [ "id", SqlType.String s.ItemTypeId.Str; "org", SqlType.String org ]
                    |> Db.querySingle (fun rd -> rd.ReadInt32 "tracking_type")
                    |> Result.requireSome (ApiError.withTarget ApiErrorCode.ItemTypeNotFound s.ItemTypeId.Str)

                if tracking <> int TrackingType.Serialized then
                    return! Error(ApiError.withTarget ApiErrorCode.ValidationFailed s.ItemTypeId.Str)

                match conn |> Db.newCommand "SELECT id FROM serialized_items WHERE organization_id=@org AND barcode=@bc"
                      |> Db.setTransaction tx |> Db.setParams [ "org", SqlType.String org; "bc", SqlType.String s.Barcode.Value ]
                      |> Db.querySingle (fun rd -> rd.ReadString "id") with
                | Some cid -> return! Error(ApiError.withTarget ApiErrorCode.BarcodeAlreadyAssigned cid)
                | None -> ()

                match s.SerialNumber |> Option.bind (fun sn ->
                      conn |> Db.newCommand "SELECT id FROM serialized_items WHERE organization_id=@org AND serial_number=@sn"
                      |> Db.setTransaction tx |> Db.setParams [ "org", SqlType.String org; "sn", SqlType.String sn ]
                      |> Db.querySingle (fun rd -> rd.ReadString "id")) with
                | Some cid -> return! Error(ApiError.withTarget ApiErrorCode.SerialNumberAlreadyAssigned cid)
                | None -> ()

                let newId = Guid.NewGuid().ToString("D")
                conn |> Db.newCommand "INSERT INTO serialized_items (id, organization_id, item_type_id, serial_number, barcode, current_holder_id, status, created_at, updated_at) VALUES (@id, @org, @tid, @sn, @bc, @hid, 0, @now, @now)"
                |> Db.setTransaction tx |> Db.setParams [ "id", SqlType.String newId; "org", SqlType.String org; "tid", SqlType.String s.ItemTypeId.Str; "sn", Sql.strOpt s.SerialNumber; "bc", SqlType.String s.Barcode.Value; "hid", Sql.strOpt (destOpt |> Option.map _.Str); "now", SqlType.String now ] |> Db.exec
                return newId
            })

    let private moveQuantitativeStock (conn: SqliteConnection) tx org (parties: TransferParties) now (lines: QuantSpec list) =
        lines |> List.traverseResultM (fun q ->
            result {
                let! tracking =
                    conn |> Db.newCommand "SELECT tracking_type FROM item_types WHERE id=@id AND organization_id=@org"
                    |> Db.setTransaction tx |> Db.setParams [ "id", SqlType.String q.ItemTypeId.Str; "org", SqlType.String org ]
                    |> Db.querySingle (fun rd -> rd.ReadInt32 "tracking_type")
                    |> Result.requireSome (ApiError.withTarget ApiErrorCode.ItemTypeNotFound q.ItemTypeId.Str)

                if tracking <> int TrackingType.Quantitative then
                    return! Error(ApiError.withTarget ApiErrorCode.ValidationFailed q.ItemTypeId.Str)

                match parties.SourceOpt with
                | None -> ()
                | Some src ->
                    let! (stockId, currQty) =
                        conn |> Db.newCommand "SELECT id, quantity FROM inventory_stocks WHERE organization_id=@org AND holder_id=@hid AND item_type_id=@tid"
                        |> Db.setTransaction tx |> Db.setParams [ "org", SqlType.String org; "hid", SqlType.String src.Str; "tid", SqlType.String q.ItemTypeId.Str ]
                        |> Db.querySingle (fun rd -> rd.ReadString "id", rd.ReadInt32 "quantity")
                        |> Result.requireSome (ApiError.withTarget ApiErrorCode.InsufficientQuantitativeStock q.ItemTypeId.Str)
                    if currQty < q.Quantity.Value then
                        return! Error(ApiError.withTarget ApiErrorCode.InsufficientQuantitativeStock q.ItemTypeId.Str)
                    conn |> Db.newCommand "UPDATE inventory_stocks SET quantity=@q, updated_at=@now WHERE id=@id"
                    |> Db.setTransaction tx |> Db.setParams [ "q", SqlType.Int32(currQty - q.Quantity.Value); "now", SqlType.String now; "id", SqlType.String stockId ] |> Db.exec

                match parties.DestOpt with
                | None -> ()
                | Some dst ->
                    match conn |> Db.newCommand "SELECT id, quantity FROM inventory_stocks WHERE organization_id=@org AND holder_id=@hid AND item_type_id=@tid"
                          |> Db.setTransaction tx |> Db.setParams [ "org", SqlType.String org; "hid", SqlType.String dst.Str; "tid", SqlType.String q.ItemTypeId.Str ]
                          |> Db.querySingle (fun rd -> rd.ReadString "id", rd.ReadInt32 "quantity") with
                    | Some(stockId, currQty) ->
                        conn |> Db.newCommand "UPDATE inventory_stocks SET quantity=@q, updated_at=@now WHERE id=@id"
                        |> Db.setTransaction tx |> Db.setParams [ "q", SqlType.Int32(currQty + q.Quantity.Value); "now", SqlType.String now; "id", SqlType.String stockId ] |> Db.exec
                    | None ->
                        conn |> Db.newCommand "INSERT INTO inventory_stocks (id, organization_id, item_type_id, holder_id, quantity, updated_at) VALUES (@id, @org, @tid, @hid, @q, @now)"
                        |> Db.setTransaction tx |> Db.setParams [ "id", SqlType.String(Guid.NewGuid().ToString("D")); "org", SqlType.String org; "tid", SqlType.String q.ItemTypeId.Str; "hid", SqlType.String dst.Str; "q", SqlType.Int32 q.Quantity.Value; "now", SqlType.String now ] |> Db.exec
            })
        |> Result.map ignore

    let private updateCustodyAndLocations (conn: SqliteConnection) tx org adminId (cmd: ValidatedTransfer) now (serializedIds: string list) =
        let destStr = cmd.Parties.DestOpt |> Option.map _.Str
        for itemId in serializedIds do
            match cmd.LocationSync with
            | KeepLocation ->
                conn |> Db.newCommand "UPDATE serialized_items SET current_holder_id=@dst, updated_at=@now WHERE id=@id"
                |> Db.setTransaction tx |> Db.setParams [ "dst", Sql.strOpt destStr; "now", SqlType.String now; "id", SqlType.String itemId ] |> Db.exec
            | SyncTo target ->
                let fromHolder, fromText =
                    conn |> Db.newCommand "SELECT location_holder_id, location_custom_text FROM serialized_items WHERE id=@id"
                    |> Db.setTransaction tx |> Db.setParams [ "id", SqlType.String itemId ]
                    |> Db.querySingle (fun rd -> rd.ReadStringOption "location_holder_id", rd.ReadStringOption "location_custom_text")
                    |> Option.defaultValue (None, None)
                let toHolder, toText = target.ToDbCols()
                conn |> Db.newCommand "UPDATE serialized_items SET current_holder_id=@dst, location_holder_id=@th, location_custom_text=@tt, updated_at=@now WHERE id=@id"
                |> Db.setTransaction tx |> Db.setParams [ "dst", Sql.strOpt destStr; "th", Sql.strOpt toHolder; "tt", Sql.strOpt toText; "now", SqlType.String now; "id", SqlType.String itemId ] |> Db.exec
                conn |> Db.newCommand "INSERT INTO serialized_item_location_logs (id, organization_id, serialized_item_id, admin_holder_id, from_location_holder_id, from_location_custom_text, to_location_holder_id, to_location_custom_text, notes, recorded_at) VALUES (@id, @org, @item, @adm, @fh, @ft, @th, @tt, @notes, @now)"
                |> Db.setTransaction tx |> Db.setParams [ "id", SqlType.String(Guid.NewGuid().ToString("D")); "org", SqlType.String org; "item", SqlType.String itemId; "adm", SqlType.String adminId; "fh", Sql.strOpt fromHolder; "ft", Sql.strOpt fromText; "th", Sql.strOpt toHolder; "tt", Sql.strOpt toText; "notes", Sql.strOpt cmd.Parties.NotesOpt; "now", SqlType.String now ] |> Db.exec
        match cmd.LocationSync with SyncTo _ -> not serializedIds.IsEmpty | KeepLocation -> false

    let private insertVoucher (conn: SqliteConnection) tx org adminId (cmd: ValidatedTransfer) (nowUtc: DateTimeOffset) (serializedIds: string list) locUpdated =
        let now = nowUtc.ToString("O")
        let count =
            conn |> Db.newCommand "SELECT COUNT(*) AS cnt FROM custody_transfers WHERE organization_id=@org"
            |> Db.setTransaction tx |> Db.setParams [ "org", SqlType.String org ]
            |> Db.querySingle (fun rd -> rd.ReadInt32 "cnt") |> Option.defaultValue 0
        let transferId, voucherNo = Guid.NewGuid().ToString("D"), sprintf "TR-%d-%05d" nowUtc.Year (count + 1)

        conn |> Db.newCommand "INSERT INTO custody_transfers (id, organization_id, transfer_template_id, transfer_number, admin_holder_id, source_holder_id, dest_holder_id, notes, transferred_at) VALUES (@id, @org, @tpl, @no, @adm, @src, @dst, @notes, @now)"
        |> Db.setTransaction tx |> Db.setParams [ "id", SqlType.String transferId; "org", SqlType.String org; "tpl", Sql.strOpt (cmd.TemplateId |> Option.map _.Str); "no", SqlType.String voucherNo; "adm", SqlType.String adminId; "src", Sql.strOpt (cmd.Parties.SourceOpt |> Option.map _.Str); "dst", Sql.strOpt (cmd.Parties.DestOpt |> Option.map _.Str); "notes", Sql.strOpt cmd.Parties.NotesOpt; "now", SqlType.String now ] |> Db.exec

        let allLines =
            [ for sid in serializedIds -> Some sid, None, 1
              for q in cmd.QuantLines -> None, Some q.ItemTypeId.Str, q.Quantity.Value ]

        allLines |> List.iteri (fun idx (serOpt, typeOpt, qty) ->
            conn |> Db.newCommand "INSERT INTO custody_transfer_lines (transfer_id, line_number, serialized_item_id, item_type_id, quantity) VALUES (@tid, @ln, @ser, @typ, @qty)"
            |> Db.setTransaction tx |> Db.setParams [ "tid", SqlType.String transferId; "ln", SqlType.Int32(idx + 1); "ser", Sql.strOpt serOpt; "typ", Sql.strOpt typeOpt; "qty", SqlType.Int32 qty ] |> Db.exec)

        { TransferId = transferId; TransferNumber = voucherNo; TransferredAt = now; LinesCount = allLines.Length; PhysicalLocationUpdated = locUpdated }

    let commitTransfer (db: IDbContext) (auth: AuthContext) (idempotencyKeyOpt: string option) (cmd: ValidatedTransfer) =
        db.WriteTxAsync(fun conn tx ->
            task {
                let org, adminId, nowUtc = auth.ActiveOrg.OrgId.Str, auth.ActiveOrg.HolderId.Str, DateTimeOffset.UtcNow
                let now = nowUtc.ToString("O")

                match findCachedIdempotency conn tx org idempotencyKeyOpt with
                | Some cached -> return Ok cached
                | None ->
                    return result {
                        do! verifyHolderActive conn tx org cmd.Parties.SourceOpt
                        do! verifyHolderActive conn tx org cmd.Parties.DestOpt
                        match cmd.LocationSync with
                        | SyncTo(AtHolder h) | SyncTo(AtHolderAndText(h, _)) -> do! verifyHolderActive conn tx org (Some h)
                        | _ -> ()

                        let! existingSerIds = verifyExistingSerialized conn tx org cmd.Parties.SourceOpt cmd.ExistingSerialized
                        let! intakeSerIds = insertIntakeSerialized conn tx org cmd.Parties.DestOpt now cmd.IntakeSerialized
                        do! moveQuantitativeStock conn tx org cmd.Parties now cmd.QuantLines

                        let allSerIds = existingSerIds @ intakeSerIds
                        let locUpdated = updateCustodyAndLocations conn tx org adminId cmd now allSerIds
                        let response = insertVoucher conn tx org adminId cmd nowUtc allSerIds locUpdated

                        match idempotencyKeyOpt with
                        | None -> ()
                        | Some key ->
                            let json = JsonSerializer.Serialize(response, Http.snakeCaseJsonOptions)
                            conn |> Db.newCommand "INSERT INTO idempotency_keys (organization_id, idempotency_key, endpoint_name, response_json, created_at) VALUES (@org, @k, 'POST /transfers', @json, @now)"
                            |> Db.setTransaction tx |> Db.setParams [ "org", SqlType.String org; "k", SqlType.String key; "json", SqlType.String json; "now", SqlType.String now ] |> Db.exec

                        return response
                    }
            })
