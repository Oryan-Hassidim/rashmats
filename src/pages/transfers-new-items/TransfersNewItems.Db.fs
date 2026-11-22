namespace Rashmatz.Pages.TransfersNewItems

open System
open System.Text.Json
open System.Threading.Tasks
open Donald
open FsToolkit.ErrorHandling
open Rashmatz.Shared
open Rashmatz.Shared.DomainTypes
open Rashmatz.Shared.Schema
open Rashmatz.Pages.TransfersNewItems.Domain

/// Step 6C Database Operations (`TransfersNewItems.Db.fs`):
/// Implements:
/// 1. Smart & Focused Bootstrap loading (`GET /pages/transfers-new-items`)
/// 2. Checking unassigned barcode / serial number (`QUERY /serialized-items/check-unassigned`)
/// 3. Creating a new catalog ItemType (`POST /item-types`)
/// 4. Atomic Transfer Execution with `Idempotency-Key` and Physical Location Sync (`POST /transfers`)
module Db =

    let loadStep6CBootstrapAsync
        (db: IDbContext)
        (orgId: OrgId)
        (sourceHolderIdOpt: HolderId option)
        (destHolderIdOpt: HolderId option)
        (templateIdOpt: TemplateId option)
        : Task<Result<TransfersNewItemsPageDto, ApiError>> =
        task {
            use conn = db.OpenReadConnection()
            let orgIdStr = orgId.ToDbString()

            // 1. Load categories via generated schema reader
            let categoryRows =
                conn
                |> Db.newCommand
                    """
                    SELECT id, organization_id, name, description, created_at, updated_at
                    FROM item_categories
                    WHERE organization_id = @org_id
                    ORDER BY name ASC
                    """
                |> Db.setParams [ "org_id", SqlType.String orgIdStr ]
                |> Db.query main.Readers.read_item_categories

            let categories =
                categoryRows
                |> List.map (fun (c: main.item_categories) ->
                    ({ Id = c.id
                       Name = c.name
                       Description = c.description }
                    : Rashmatz.Pages.MyCustody.Server.CategorySummaryDto))

            // 2. If source_holder_id is provided, load all serialized items & quantitative balances at source
            let sourceSerialized, sourceQuant =
                match sourceHolderIdOpt with
                | None -> [], []
                | Some srcId ->
                    let srcIdStr = srcId.ToDbString()

                    let serItems =
                        conn
                        |> Db.newCommand
                            """
                            SELECT
                                si.id,
                                si.item_type_id,
                                it.name AS item_type_name,
                                it.category_id,
                                it.unit_of_measure,
                                si.barcode,
                                si.serial_number,
                                si.status,
                                si.location_holder_id,
                                si.location_custom_text
                            FROM serialized_items si
                            INNER JOIN item_types it ON it.id = si.item_type_id
                            WHERE si.organization_id = @org_id
                              AND si.current_holder_id = @holder_id
                            ORDER BY it.name ASC, si.barcode ASC
                            """
                        |> Db.setParams
                            [ "org_id", SqlType.String orgIdStr
                              "holder_id", SqlType.String srcIdStr ]
                        |> Db.query (fun rd ->
                            { Id = rd.ReadString "id"
                              ItemTypeId = rd.ReadString "item_type_id"
                              ItemTypeName = rd.ReadString "item_type_name"
                              CategoryId = rd.ReadStringOption "category_id"
                              UnitOfMeasure = rd.ReadString "unit_of_measure"
                              Barcode = rd.ReadString "barcode"
                              SerialNumber = rd.ReadStringOption "serial_number"
                              Status = rd.ReadInt32 "status"
                              LocationHolderId = rd.ReadStringOption "location_holder_id"
                              LocationCustomText = rd.ReadStringOption "location_custom_text" })

                    let quantBalances =
                        conn
                        |> Db.newCommand
                            """
                            SELECT
                                st.item_type_id,
                                it.name AS item_type_name,
                                it.category_id,
                                it.unit_of_measure,
                                st.quantity
                            FROM inventory_stocks st
                            INNER JOIN item_types it ON it.id = st.item_type_id
                            WHERE st.organization_id = @org_id
                              AND st.holder_id = @holder_id
                              AND st.quantity > 0
                            ORDER BY it.name ASC
                            """
                        |> Db.setParams
                            [ "org_id", SqlType.String orgIdStr
                              "holder_id", SqlType.String srcIdStr ]
                        |> Db.query (fun rd ->
                            { ItemTypeId = rd.ReadString "item_type_id"
                              ItemTypeName = rd.ReadString "item_type_name"
                              CategoryId = rd.ReadStringOption "category_id"
                              UnitOfMeasure = rd.ReadString "unit_of_measure"
                              Quantity = rd.ReadInt32 "quantity" })

                    serItems, quantBalances

            // 3. If dest_holder_id is provided, load current balances at destination for Before/After comparison
            let destBalances =
                match destHolderIdOpt with
                | None -> []
                | Some dstId ->
                    let dstIdStr = dstId.ToDbString()

                    conn
                    |> Db.newCommand
                        """
                        SELECT
                            it.id AS item_type_id,
                            it.tracking_type,
                            CASE
                                WHEN it.tracking_type = 1 THEN (
                                    SELECT COUNT(*)
                                    FROM serialized_items si
                                    WHERE si.organization_id = @org_id
                                      AND si.item_type_id = it.id
                                      AND si.current_holder_id = @holder_id
                                )
                                ELSE COALESCE((
                                    SELECT st.quantity
                                    FROM inventory_stocks st
                                    WHERE st.organization_id = @org_id
                                      AND st.item_type_id = it.id
                                      AND st.holder_id = @holder_id
                                ), 0)
                            END AS current_quantity
                        FROM item_types it
                        WHERE it.organization_id = @org_id
                        """
                    |> Db.setParams
                        [ "org_id", SqlType.String orgIdStr
                          "holder_id", SqlType.String dstIdStr ]
                    |> Db.query (fun rd ->
                        { ItemTypeId = rd.ReadString "item_type_id"
                          TrackingType = rd.ReadInt32 "tracking_type"
                          CurrentQuantity = rd.ReadInt32 "current_quantity" })

            // 4. If template_id is provided, load the template header AND its lines
            let selectedTemplateResult =
                match templateIdOpt with
                | None -> Ok None
                | Some tplId ->
                    let tplIdStr = tplId.ToDbString()

                    let headerOpt =
                        conn
                        |> Db.newCommand
                            """
                            SELECT id, name, description, default_source_holder_id, default_dest_holder_id
                            FROM transfer_templates
                            WHERE id = @id AND organization_id = @org_id AND is_active = 1
                            """
                        |> Db.setParams
                            [ "id", SqlType.String tplIdStr
                              "org_id", SqlType.String orgIdStr ]
                        |> Db.querySingle (fun rd ->
                            rd.ReadString "id",
                            rd.ReadString "name",
                            rd.ReadStringOption "description",
                            rd.ReadStringOption "default_source_holder_id",
                            rd.ReadStringOption "default_dest_holder_id")

                    match headerOpt with
                    | None -> Error(ApiError.withTarget ApiErrorCode.TemplateNotFound tplIdStr)
                    | Some(id, name, descOpt, defSrcOpt, defDstOpt) ->
                        let lines =
                            conn
                            |> Db.newCommand
                                """
                                SELECT
                                    tl.line_number,
                                    tl.item_type_id,
                                    it.name AS item_type_name,
                                    it.tracking_type,
                                    tl.quantity
                                FROM transfer_template_lines tl
                                INNER JOIN item_types it ON it.id = tl.item_type_id
                                WHERE tl.template_id = @tpl_id
                                ORDER BY tl.line_number ASC
                                """
                            |> Db.setParams [ "tpl_id", SqlType.String tplIdStr ]
                            |> Db.query (fun rd ->
                                { LineNumber = rd.ReadInt32 "line_number"
                                  ItemTypeId = rd.ReadString "item_type_id"
                                  ItemTypeName = rd.ReadString "item_type_name"
                                  TrackingType = rd.ReadInt32 "tracking_type"
                                  Quantity = rd.ReadInt32 "quantity" })

                        Ok(
                            Some
                                { Id = id
                                  Name = name
                                  Description = descOpt
                                  DefaultSourceHolderId = defSrcOpt
                                  DefaultDestHolderId = defDstOpt
                                  Lines = lines }
                        )

            match selectedTemplateResult with
            | Error e -> return Error e
            | Ok selectedTemplateOpt ->
                // 5. Only when source_id is None (External Intake mode), return full item_types catalog
                let intakeCatalogOpt =
                    match sourceHolderIdOpt with
                    | Some _ -> None
                    | None ->
                        let catalog =
                            conn
                            |> Db.newCommand
                                """
                                SELECT
                                    it.id,
                                    it.category_id,
                                    ic.name AS category_name,
                                    it.name,
                                    it.tracking_type,
                                    it.unit_of_measure,
                                    it.description
                                FROM item_types it
                                LEFT JOIN item_categories ic ON ic.id = it.category_id
                                WHERE it.organization_id = @org_id
                                ORDER BY it.name ASC
                                """
                            |> Db.setParams [ "org_id", SqlType.String orgIdStr ]
                            |> Db.query (fun rd ->
                                { Id = rd.ReadString "id"
                                  CategoryId = rd.ReadStringOption "category_id"
                                  CategoryName = rd.ReadStringOption "category_name"
                                  Name = rd.ReadString "name"
                                  TrackingType = rd.ReadInt32 "tracking_type"
                                  UnitOfMeasure = rd.ReadString "unit_of_measure"
                                  Description = rd.ReadStringOption "description" })

                        Some catalog

                return
                    Ok
                        { SourceHolderId = sourceHolderIdOpt |> Option.map (fun h -> h.ToDbString())
                          DestHolderId = destHolderIdOpt |> Option.map (fun h -> h.ToDbString())
                          Categories = categories
                          SourceSerializedItems = sourceSerialized
                          SourceQuantitativeBalances = sourceQuant
                          DestBalances = destBalances
                          SelectedTemplate = selectedTemplateOpt
                          IntakeCatalogItemTypes = intakeCatalogOpt }
        }

    let checkUnassignedItem
        (db: IDbContext)
        (orgId: OrgId)
        (req: CheckUnassignedItemQueryDto)
        : Result<CheckUnassignedItemResponseDto, ApiError> =
        use conn = db.OpenReadConnection()
        let orgIdStr = orgId.ToDbString()
        let barcodeOpt = SmartConstructors.createOptionalTrimmedString req.Barcode
        let serialOpt = SmartConstructors.createOptionalTrimmedString req.SerialNumber

        let barcodeConflictOpt =
            match barcodeOpt with
            | None -> None
            | Some bc ->
                conn
                |> Db.newCommand
                    "SELECT id FROM serialized_items WHERE organization_id = @org_id AND barcode = @barcode"
                |> Db.setParams
                    [ "org_id", SqlType.String orgIdStr
                      "barcode", SqlType.String bc ]
                |> Db.querySingle (fun rd -> rd.ReadString "id")

        let serialConflictOpt =
            match serialOpt with
            | None -> None
            | Some sn ->
                conn
                |> Db.newCommand
                    "SELECT id FROM serialized_items WHERE organization_id = @org_id AND serial_number = @serial"
                |> Db.setParams
                    [ "org_id", SqlType.String orgIdStr
                      "serial", SqlType.String sn ]
                |> Db.querySingle (fun rd -> rd.ReadString "id")

        Ok
            { BarcodeAvailable = Option.isNone barcodeConflictOpt
              SerialNumberAvailable = Option.isNone serialConflictOpt
              ConflictingItemId = Option.orElse serialConflictOpt barcodeConflictOpt }

    let createItemTypeAsync
        (db: IDbContext)
        (orgId: OrgId)
        (cmd: ValidatedCreateItemTypeCommand)
        : Task<Result<CatalogItemTypeDto, ApiError>> =
        db.ExecuteWriteTransactionAsync(fun conn tx ->
            task {
                let orgIdStr = orgId.ToDbString()
                let nowIso = DateTimeOffset.UtcNow.ToString("O")

                let resolvedCategoryResult =
                    match cmd.Category with
                    | Uncategorized -> Ok(None, None)
                    | ExistingCategory catId ->
                        let catIdStr = catId.ToDbString()

                        let catNameOpt =
                            conn
                            |> Db.newCommand
                                "SELECT name FROM item_categories WHERE id = @id AND organization_id = @org_id"
                            |> Db.setTransaction tx
                            |> Db.setParams
                                [ "id", SqlType.String catIdStr
                                  "org_id", SqlType.String orgIdStr ]
                            |> Db.querySingle (fun rd -> rd.ReadString "name")

                        match catNameOpt with
                        | Some catName -> Ok(Some catIdStr, Some catName)
                        | None -> Error(ApiError.withTarget ApiErrorCode.CategoryNotFound catIdStr)

                    | CreateNewCategory newCatName ->
                        let existingCatOpt =
                            conn
                            |> Db.newCommand
                                "SELECT id, name FROM item_categories WHERE organization_id = @org_id AND LOWER(name) = LOWER(@name)"
                            |> Db.setTransaction tx
                            |> Db.setParams
                                [ "org_id", SqlType.String orgIdStr
                                  "name", SqlType.String newCatName.Value ]
                            |> Db.querySingle (fun rd -> rd.ReadString "id", rd.ReadString "name")

                        match existingCatOpt with
                        | Some(existingId, existingName) -> Ok(Some existingId, Some existingName)
                        | None ->
                            let newCatId = Guid.NewGuid().ToString("D")

                            conn
                            |> Db.newCommand
                                """
                                INSERT INTO item_categories (id, organization_id, name, description, created_at, updated_at)
                                VALUES (@id, @org_id, @name, NULL, @now, @now)
                                """
                            |> Db.setTransaction tx
                            |> Db.setParams
                                [ "id", SqlType.String newCatId
                                  "org_id", SqlType.String orgIdStr
                                  "name", SqlType.String newCatName.Value
                                  "now", SqlType.String nowIso ]
                            |> Db.exec

                            Ok(Some newCatId, Some newCatName.Value)

                match resolvedCategoryResult with
                | Error e -> return Error e
                | Ok(catIdOpt, catNameOpt) ->
                    let newItemTypeId = Guid.NewGuid().ToString("D")

                    conn
                    |> Db.newCommand
                        """
                        INSERT INTO item_types (
                            id, organization_id, category_id, name, tracking_type,
                            unit_of_measure, description, created_at, updated_at
                        )
                        VALUES (
                            @id, @org_id, @cat_id, @name, @tracking,
                            @uom, @desc, @now, @now
                        )
                        """
                    |> Db.setTransaction tx
                    |> Db.setParams
                        [ "id", SqlType.String newItemTypeId
                          "org_id", SqlType.String orgIdStr
                          "cat_id",
                          (match catIdOpt with
                           | Some c -> SqlType.String c
                           | None -> SqlType.Null)
                          "name", SqlType.String cmd.Name.Value
                          "tracking", SqlType.Int32(int cmd.TrackingType)
                          "uom", SqlType.String cmd.UnitOfMeasure
                          "desc",
                          (match cmd.Description with
                           | Some d -> SqlType.String d
                           | None -> SqlType.Null)
                          "now", SqlType.String nowIso ]
                    |> Db.exec

                    return
                        Ok
                            { Id = newItemTypeId
                              CategoryId = catIdOpt
                              CategoryName = catNameOpt
                              Name = cmd.Name.Value
                              TrackingType = int cmd.TrackingType
                              UnitOfMeasure = cmd.UnitOfMeasure
                              Description = cmd.Description }
            })

    let commitTransferAsync
        (db: IDbContext)
        (auth: AuthenticatedContext)
        (idempotencyKeyOpt: string option)
        (cmd: ValidatedCommitTransferCommand)
        : Task<Result<CommitTransferResponseDto, ApiError>> =
        db.ExecuteWriteTransactionAsync(fun conn tx ->
            task {
                let orgIdStr = auth.ActiveOrg.OrganizationId.ToDbString()
                let adminHolderIdStr = auth.ActiveOrg.HolderId.ToDbString()

                // 1. Check Idempotency-Key first
                let cachedResponseOpt =
                    match idempotencyKeyOpt with
                    | None -> None
                    | Some key ->
                        conn
                        |> Db.newCommand
                            """
                            SELECT response_json
                            FROM idempotency_keys
                            WHERE organization_id = @org_id
                              AND idempotency_key = @key
                              AND endpoint_name = 'POST /transfers'
                            """
                        |> Db.setTransaction tx
                        |> Db.setParams
                            [ "org_id", SqlType.String orgIdStr
                              "key", SqlType.String key ]
                        |> Db.querySingle (fun rd -> rd.ReadString "response_json")

                match cachedResponseOpt with
                | Some json ->
                    match JsonSerializer.Deserialize<CommitTransferResponseDto>(json) with
                    | null -> return Error(ApiError.single ApiErrorCode.InternalError)
                    | dto -> return Ok dto
                | None ->
                    let nowUtc = DateTimeOffset.UtcNow
                    let nowIso = nowUtc.ToString("O")

                    let verifyHolder (holderOpt: HolderId option) =
                        match holderOpt with
                        | None -> Ok()
                        | Some h ->
                            let hStr = h.ToDbString()

                            let rowOpt =
                                conn
                                |> Db.newCommand
                                    "SELECT is_active FROM custody_holders WHERE id = @id AND organization_id = @org_id"
                                |> Db.setTransaction tx
                                |> Db.setParams
                                    [ "id", SqlType.String hStr
                                      "org_id", SqlType.String orgIdStr ]
                                |> Db.querySingle (fun rd -> rd.ReadInt32 "is_active" = 1)

                            match rowOpt with
                            | None -> Error(ApiError.withTarget ApiErrorCode.HolderNotFound hStr)
                            | Some false -> Error(ApiError.withTarget ApiErrorCode.HolderInactive hStr)
                            | Some true -> Ok()

                    let partiesCheck =
                        result {
                            do! verifyHolder cmd.Parties.SourceHolderIdOpt
                            do! verifyHolder cmd.Parties.DestHolderIdOpt

                            match cmd.LocationSync with
                            | SyncToLocation(AtHolderOnly locH)
                            | SyncToLocation(AtHolderWithText(locH, _)) -> do! verifyHolder (Some locH)
                            | _ -> ()
                        }

                    match partiesCheck with
                    | Error e -> return Error e
                    | Ok() ->
                        let mutable abortError: ApiError option = None
                        let resolvedSerializedIds = ResizeArray<string>()

                        for serId in cmd.ExistingSerializedItemIds do
                            if Option.isNone abortError then
                                let itemStr = serId.ToDbString()

                                let itemRowOpt =
                                    conn
                                    |> Db.newCommand
                                        """
                                        SELECT current_holder_id, location_holder_id, location_custom_text
                                        FROM serialized_items
                                        WHERE id = @id AND organization_id = @org_id
                                        """
                                    |> Db.setTransaction tx
                                    |> Db.setParams
                                        [ "id", SqlType.String itemStr
                                          "org_id", SqlType.String orgIdStr ]
                                    |> Db.querySingle (fun rd ->
                                        rd.ReadStringOption "current_holder_id",
                                        rd.ReadStringOption "location_holder_id",
                                        rd.ReadStringOption "location_custom_text")

                                match itemRowOpt, cmd.Parties.SourceHolderIdOpt with
                                | None, _ ->
                                    abortError <- Some(ApiError.withTarget ApiErrorCode.SerializedItemNotFound itemStr)
                                | Some(currentHolderOpt, _, _), Some expectedSrc ->
                                    let expectedStr = expectedSrc.ToDbString()

                                    if currentHolderOpt <> Some expectedStr then
                                        abortError <-
                                            Some(ApiError.withTarget ApiErrorCode.ItemNotOwnedBySourceHolder itemStr)
                                    else
                                        resolvedSerializedIds.Add(itemStr)
                                | Some _, None ->
                                    abortError <- Some(ApiError.withTarget ApiErrorCode.ValidationFailed itemStr)

                        for intakeSpec in cmd.NewIntakeSerializedSpecs do
                            if Option.isNone abortError then
                                let itemTypeStr = intakeSpec.ItemTypeId.ToDbString()

                                let itemTypeRowOpt =
                                    conn
                                    |> Db.newCommand
                                        "SELECT tracking_type FROM item_types WHERE id = @id AND organization_id = @org_id"
                                    |> Db.setTransaction tx
                                    |> Db.setParams
                                        [ "id", SqlType.String itemTypeStr
                                          "org_id", SqlType.String orgIdStr ]
                                    |> Db.querySingle (fun rd -> rd.ReadInt32 "tracking_type")

                                match itemTypeRowOpt with
                                | None ->
                                    abortError <- Some(ApiError.withTarget ApiErrorCode.ItemTypeNotFound itemTypeStr)
                                | Some tt when tt <> int TrackingType.Serialized ->
                                    abortError <- Some(ApiError.withTarget ApiErrorCode.ValidationFailed itemTypeStr)
                                | Some _ ->
                                    let existingBarcodeOpt =
                                        conn
                                        |> Db.newCommand
                                            "SELECT id FROM serialized_items WHERE organization_id = @org_id AND barcode = @bc"
                                        |> Db.setTransaction tx
                                        |> Db.setParams
                                            [ "org_id", SqlType.String orgIdStr
                                              "bc", SqlType.String intakeSpec.Barcode.Value ]
                                        |> Db.querySingle (fun rd -> rd.ReadString "id")

                                    match existingBarcodeOpt with
                                    | Some conflictId ->
                                        abortError <-
                                            Some(ApiError.withTarget ApiErrorCode.BarcodeAlreadyAssigned conflictId)
                                    | None ->
                                        let existingSerialOpt =
                                            match intakeSpec.SerialNumber with
                                            | None -> None
                                            | Some sn ->
                                                conn
                                                |> Db.newCommand
                                                    "SELECT id FROM serialized_items WHERE organization_id = @org_id AND serial_number = @sn"
                                                |> Db.setTransaction tx
                                                |> Db.setParams
                                                    [ "org_id", SqlType.String orgIdStr
                                                      "sn", SqlType.String sn ]
                                                |> Db.querySingle (fun rd -> rd.ReadString "id")

                                        match existingSerialOpt with
                                        | Some conflictId ->
                                            abortError <-
                                                Some(
                                                    ApiError.withTarget
                                                        ApiErrorCode.SerialNumberAlreadyAssigned
                                                        conflictId
                                                )
                                        | None ->
                                            let newSerId = Guid.NewGuid().ToString("D")
                                            let destHolderStrOpt =
                                                cmd.Parties.DestHolderIdOpt |> Option.map (fun d -> d.ToDbString())

                                            conn
                                            |> Db.newCommand
                                                """
                                                INSERT INTO serialized_items (
                                                    id, organization_id, item_type_id, serial_number, barcode,
                                                    current_holder_id, location_holder_id, location_custom_text,
                                                    status, created_at, updated_at
                                                )
                                                VALUES (
                                                    @id, @org_id, @item_type_id, @serial, @barcode,
                                                    @holder_id, NULL, NULL,
                                                    0, @now, @now
                                                )
                                                """
                                            |> Db.setTransaction tx
                                            |> Db.setParams
                                                [ "id", SqlType.String newSerId
                                                  "org_id", SqlType.String orgIdStr
                                                  "item_type_id", SqlType.String itemTypeStr
                                                  "serial",
                                                  (match intakeSpec.SerialNumber with
                                                   | Some s -> SqlType.String s
                                                   | None -> SqlType.Null)
                                                  "barcode", SqlType.String intakeSpec.Barcode.Value
                                                  "holder_id",
                                                  (match destHolderStrOpt with
                                                   | Some d -> SqlType.String d
                                                   | None -> SqlType.Null)
                                                  "now", SqlType.String nowIso ]
                                            |> Db.exec

                                            resolvedSerializedIds.Add(newSerId)

                        for qLine in cmd.QuantitativeLines do
                            if Option.isNone abortError then
                                let itemTypeStr = qLine.ItemTypeId.ToDbString()
                                let qty = qLine.Quantity.Value

                                let trackingOpt =
                                    conn
                                    |> Db.newCommand
                                        "SELECT tracking_type FROM item_types WHERE id = @id AND organization_id = @org_id"
                                    |> Db.setTransaction tx
                                    |> Db.setParams
                                        [ "id", SqlType.String itemTypeStr
                                          "org_id", SqlType.String orgIdStr ]
                                    |> Db.querySingle (fun rd -> rd.ReadInt32 "tracking_type")

                                match trackingOpt with
                                | None ->
                                    abortError <- Some(ApiError.withTarget ApiErrorCode.ItemTypeNotFound itemTypeStr)
                                | Some tt when tt <> int TrackingType.Quantitative ->
                                    abortError <- Some(ApiError.withTarget ApiErrorCode.ValidationFailed itemTypeStr)
                                | Some _ ->
                                    match cmd.Parties.SourceHolderIdOpt with
                                    | None -> ()
                                    | Some srcHolder ->
                                        let srcStr = srcHolder.ToDbString()

                                        let currentStockOpt =
                                            conn
                                            |> Db.newCommand
                                                """
                                                SELECT id, quantity
                                                FROM inventory_stocks
                                                WHERE organization_id = @org_id
                                                  AND holder_id = @holder_id
                                                  AND item_type_id = @item_type_id
                                                """
                                            |> Db.setTransaction tx
                                            |> Db.setParams
                                                [ "org_id", SqlType.String orgIdStr
                                                  "holder_id", SqlType.String srcStr
                                                  "item_type_id", SqlType.String itemTypeStr ]
                                            |> Db.querySingle (fun rd -> rd.ReadString "id", rd.ReadInt32 "quantity")

                                        match currentStockOpt with
                                        | None ->
                                            abortError <-
                                                Some(
                                                    ApiError.withTarget
                                                        ApiErrorCode.InsufficientQuantitativeStock
                                                        itemTypeStr
                                                )
                                        | Some(_, currentQty) when currentQty < qty ->
                                            abortError <-
                                                Some(
                                                    ApiError.withTarget
                                                        ApiErrorCode.InsufficientQuantitativeStock
                                                        itemTypeStr
                                                )
                                        | Some(stockId, currentQty) ->
                                            conn
                                            |> Db.newCommand
                                                "UPDATE inventory_stocks SET quantity = @new_qty, updated_at = @now WHERE id = @id"
                                            |> Db.setTransaction tx
                                            |> Db.setParams
                                                [ "new_qty", SqlType.Int32(currentQty - qty)
                                                  "now", SqlType.String nowIso
                                                  "id", SqlType.String stockId ]
                                            |> Db.exec

                                    if Option.isNone abortError then
                                        match cmd.Parties.DestHolderIdOpt with
                                        | None -> ()
                                        | Some dstHolder ->
                                            let dstStr = dstHolder.ToDbString()

                                            let destStockOpt =
                                                conn
                                                |> Db.newCommand
                                                    """
                                                    SELECT id, quantity
                                                    FROM inventory_stocks
                                                    WHERE organization_id = @org_id
                                                      AND holder_id = @holder_id
                                                      AND item_type_id = @item_type_id
                                                    """
                                                |> Db.setTransaction tx
                                                |> Db.setParams
                                                    [ "org_id", SqlType.String orgIdStr
                                                      "holder_id", SqlType.String dstStr
                                                      "item_type_id", SqlType.String itemTypeStr ]
                                                |> Db.querySingle (fun rd ->
                                                    rd.ReadString "id", rd.ReadInt32 "quantity")

                                            match destStockOpt with
                                            | Some(stockId, currentQty) ->
                                                conn
                                                |> Db.newCommand
                                                    "UPDATE inventory_stocks SET quantity = @new_qty, updated_at = @now WHERE id = @id"
                                                |> Db.setTransaction tx
                                                |> Db.setParams
                                                    [ "new_qty", SqlType.Int32(currentQty + qty)
                                                      "now", SqlType.String nowIso
                                                      "id", SqlType.String stockId ]
                                                |> Db.exec
                                            | None ->
                                                let newStockId = Guid.NewGuid().ToString("D")

                                                conn
                                                |> Db.newCommand
                                                    """
                                                    INSERT INTO inventory_stocks (id, organization_id, item_type_id, holder_id, quantity, updated_at)
                                                    VALUES (@id, @org_id, @item_type_id, @holder_id, @qty, @now)
                                                    """
                                                |> Db.setTransaction tx
                                                |> Db.setParams
                                                    [ "id", SqlType.String newStockId
                                                      "org_id", SqlType.String orgIdStr
                                                      "item_type_id", SqlType.String itemTypeStr
                                                      "holder_id", SqlType.String dstStr
                                                      "qty", SqlType.Int32 qty
                                                      "now", SqlType.String nowIso ]
                                                |> Db.exec

                        match abortError with
                        | Some err -> return Error err
                        | None ->
                            let destHolderStrOpt =
                                cmd.Parties.DestHolderIdOpt |> Option.map (fun d -> d.ToDbString())

                            let mutable physicalLocationUpdated = false

                            for itemStr in resolvedSerializedIds do
                                let prevLocOpt =
                                    conn
                                    |> Db.newCommand
                                        "SELECT location_holder_id, location_custom_text FROM serialized_items WHERE id = @id"
                                    |> Db.setTransaction tx
                                    |> Db.setParams [ "id", SqlType.String itemStr ]
                                    |> Db.querySingle (fun rd ->
                                        rd.ReadStringOption "location_holder_id",
                                        rd.ReadStringOption "location_custom_text")

                                match cmd.LocationSync with
                                | KeepExistingLocation ->
                                    conn
                                    |> Db.newCommand
                                        """
                                        UPDATE serialized_items
                                        SET current_holder_id = @dest_holder,
                                            updated_at = @now
                                        WHERE id = @id
                                        """
                                    |> Db.setTransaction tx
                                    |> Db.setParams
                                        [ "dest_holder",
                                          (match destHolderStrOpt with
                                           | Some d -> SqlType.String d
                                           | None -> SqlType.Null)
                                          "now", SqlType.String nowIso
                                          "id", SqlType.String itemStr ]
                                    |> Db.exec

                                | SyncToLocation targetLoc ->
                                    physicalLocationUpdated <- true
                                    let toHolderOpt, toTextOpt = targetLoc.ToDbColumns()
                                    let fromHolderOpt, fromTextOpt =
                                        prevLocOpt |> Option.defaultValue (None, None)

                                    conn
                                    |> Db.newCommand
                                        """
                                        UPDATE serialized_items
                                        SET current_holder_id = @dest_holder,
                                            location_holder_id = @to_holder,
                                            location_custom_text = @to_text,
                                            updated_at = @now
                                        WHERE id = @id
                                        """
                                    |> Db.setTransaction tx
                                    |> Db.setParams
                                        [ "dest_holder",
                                          (match destHolderStrOpt with
                                           | Some d -> SqlType.String d
                                           | None -> SqlType.Null)
                                          "to_holder",
                                          (match toHolderOpt with
                                           | Some h -> SqlType.String h
                                           | None -> SqlType.Null)
                                          "to_text",
                                          (match toTextOpt with
                                           | Some t -> SqlType.String t
                                           | None -> SqlType.Null)
                                          "now", SqlType.String nowIso
                                          "id", SqlType.String itemStr ]
                                    |> Db.exec

                                    let logId = Guid.NewGuid().ToString("D")

                                    conn
                                    |> Db.newCommand
                                        """
                                        INSERT INTO serialized_item_location_logs (
                                            id, organization_id, serialized_item_id, admin_holder_id,
                                            from_location_holder_id, from_location_custom_text,
                                            to_location_holder_id, to_location_custom_text,
                                            notes, recorded_at
                                        )
                                        VALUES (
                                            @id, @org_id, @item_id, @admin_id,
                                            @from_holder, @from_text,
                                            @to_holder, @to_text,
                                            @notes, @now
                                        )
                                        """
                                    |> Db.setTransaction tx
                                    |> Db.setParams
                                        [ "id", SqlType.String logId
                                          "org_id", SqlType.String orgIdStr
                                          "item_id", SqlType.String itemStr
                                          "admin_id", SqlType.String adminHolderIdStr
                                          "from_holder",
                                          (match fromHolderOpt with
                                           | Some h -> SqlType.String h
                                           | None -> SqlType.Null)
                                          "from_text",
                                          (match fromTextOpt with
                                           | Some t -> SqlType.String t
                                           | None -> SqlType.Null)
                                          "to_holder",
                                          (match toHolderOpt with
                                           | Some h -> SqlType.String h
                                           | None -> SqlType.Null)
                                          "to_text",
                                          (match toTextOpt with
                                           | Some t -> SqlType.String t
                                           | None -> SqlType.Null)
                                          "notes",
                                          (match cmd.Parties.NotesOpt with
                                           | Some n -> SqlType.String n
                                           | None -> SqlType.Null)
                                          "now", SqlType.String nowIso ]
                                    |> Db.exec

                            let existingTransfersCount =
                                conn
                                |> Db.newCommand
                                    "SELECT COUNT(*) AS cnt FROM custody_transfers WHERE organization_id = @org_id"
                                |> Db.setTransaction tx
                                |> Db.setParams [ "org_id", SqlType.String orgIdStr ]
                                |> Db.querySingle (fun rd -> rd.ReadInt32 "cnt")
                                |> Option.defaultValue 0

                            let voucherNumber =
                                sprintf "TR-%d-%05d" nowUtc.Year (existingTransfersCount + 1)

                            let transferId = Guid.NewGuid().ToString("D")
                            let srcHolderStrOpt =
                                cmd.Parties.SourceHolderIdOpt |> Option.map (fun s -> s.ToDbString())

                            conn
                            |> Db.newCommand
                                """
                                INSERT INTO custody_transfers (
                                    id, organization_id, transfer_template_id, transfer_number,
                                    admin_holder_id, source_holder_id, dest_holder_id, notes, transferred_at
                                )
                                VALUES (
                                    @id, @org_id, @tpl_id, @number,
                                    @admin_id, @src_id, @dst_id, @notes, @now
                                )
                                """
                            |> Db.setTransaction tx
                            |> Db.setParams
                                [ "id", SqlType.String transferId
                                  "org_id", SqlType.String orgIdStr
                                  "tpl_id",
                                  (match cmd.TemplateId with
                                   | Some t -> SqlType.String(t.ToDbString())
                                   | None -> SqlType.Null)
                                  "number", SqlType.String voucherNumber
                                  "admin_id", SqlType.String adminHolderIdStr
                                  "src_id",
                                  (match srcHolderStrOpt with
                                   | Some s -> SqlType.String s
                                   | None -> SqlType.Null)
                                  "dst_id",
                                  (match destHolderStrOpt with
                                   | Some d -> SqlType.String d
                                   | None -> SqlType.Null)
                                  "notes",
                                  (match cmd.Parties.NotesOpt with
                                   | Some n -> SqlType.String n
                                   | None -> SqlType.Null)
                                  "now", SqlType.String nowIso ]
                            |> Db.exec

                            let mutable lineNumber = 0

                            for itemStr in resolvedSerializedIds do
                                lineNumber <- lineNumber + 1

                                conn
                                |> Db.newCommand
                                    """
                                    INSERT INTO custody_transfer_lines (
                                        transfer_id, line_number, serialized_item_id, item_type_id, quantity
                                    )
                                    VALUES (@transfer_id, @line_no, @ser_id, NULL, 1)
                                    """
                                |> Db.setTransaction tx
                                |> Db.setParams
                                    [ "transfer_id", SqlType.String transferId
                                      "line_no", SqlType.Int32 lineNumber
                                      "ser_id", SqlType.String itemStr ]
                                |> Db.exec

                            for qLine in cmd.QuantitativeLines do
                                lineNumber <- lineNumber + 1

                                conn
                                |> Db.newCommand
                                    """
                                    INSERT INTO custody_transfer_lines (
                                        transfer_id, line_number, serialized_item_id, item_type_id, quantity
                                    )
                                    VALUES (@transfer_id, @line_no, NULL, @item_type_id, @qty)
                                    """
                                |> Db.setTransaction tx
                                |> Db.setParams
                                    [ "transfer_id", SqlType.String transferId
                                      "line_no", SqlType.Int32 lineNumber
                                      "item_type_id", SqlType.String(qLine.ItemTypeId.ToDbString())
                                      "qty", SqlType.Int32 qLine.Quantity.Value ]
                                |> Db.exec

                            let responseDto =
                                { TransferId = transferId
                                  TransferNumber = voucherNumber
                                  TransferredAt = nowIso
                                  LinesCount = lineNumber
                                  PhysicalLocationUpdated = physicalLocationUpdated }

                            match idempotencyKeyOpt with
                            | None -> ()
                            | Some key ->
                                let serializedResp = JsonSerializer.Serialize(responseDto)

                                conn
                                |> Db.newCommand
                                    """
                                    INSERT INTO idempotency_keys (organization_id, idempotency_key, endpoint_name, response_json, created_at)
                                    VALUES (@org_id, @key, 'POST /transfers', @json, @now)
                                    """
                                |> Db.setTransaction tx
                                |> Db.setParams
                                    [ "org_id", SqlType.String orgIdStr
                                      "key", SqlType.String key
                                      "json", SqlType.String serializedResp
                                      "now", SqlType.String nowIso ]
                                |> Db.exec

                            return Ok responseDto
            })
