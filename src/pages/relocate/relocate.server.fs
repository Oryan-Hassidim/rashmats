namespace Rashmatz.Pages.Relocate

open System
open System.Text.Json.Serialization
open System.Threading.Tasks
open Oxpecker
open Donald
open FsToolkit.ErrorHandling
open Rashmatz.Shared
open Rashmatz.Shared.DomainTypes

/// Vertical Slice for Page 5: Quick Physical Relocation (`/items/relocate`)
/// Natural home of `POST /serialized-items/{id}/relocate` (called both from `/items/relocate` and from
/// Quick Relocate on `/admin/audits/active`).
module Server =

    type RelocatedItemSummaryDto =
        { [<JsonPropertyName("serialized_item_id")>]
          SerializedItemId: string
          [<JsonPropertyName("item_type_id")>]
          ItemTypeId: string
          [<JsonPropertyName("item_type_name")>]
          ItemTypeName: string
          [<JsonPropertyName("barcode")>]
          Barcode: string
          [<JsonPropertyName("serial_number")>]
          SerialNumber: string option
          [<JsonPropertyName("status")>]
          Status: int
          [<JsonPropertyName("current_holder_id")>]
          CurrentHolderId: string option
          [<JsonPropertyName("location_holder_id")>]
          LocationHolderId: string option
          [<JsonPropertyName("location_custom_text")>]
          LocationCustomText: string option
          [<JsonPropertyName("last_relocated_at")>]
          LastRelocatedAt: string option
          [<JsonPropertyName("last_relocated_by_admin_id")>]
          LastRelocatedByAdminId: string option
          [<JsonPropertyName("last_notes")>]
          LastNotes: string option }

    type RelocatePageBootstrapDto =
        { [<JsonPropertyName("recent_relocated_items")>]
          RecentRelocatedItems: RelocatedItemSummaryDto list }

    [<CLIMutable>]
    type LookupSerializedItemsQueryDto =
        { [<JsonPropertyName("q")>]
          Query: string option }

    [<CLIMutable>]
    type RelocateItemRequestDto =
        { [<JsonPropertyName("to_location_holder_id")>]
          ToLocationHolderId: string option
          [<JsonPropertyName("to_location_custom_text")>]
          ToLocationCustomText: string option
          [<JsonPropertyName("notes")>]
          Notes: string option }

    type RelocateItemResponseDto =
        { [<JsonPropertyName("log_id")>]
          LogId: string
          [<JsonPropertyName("serialized_item_id")>]
          SerializedItemId: string
          [<JsonPropertyName("to_location_holder_id")>]
          ToLocationHolderId: string option
          [<JsonPropertyName("to_location_custom_text")>]
          ToLocationCustomText: string option
          [<JsonPropertyName("recorded_at")>]
          RecordedAt: string }

    type ValidatedRelocationCommand =
        { ItemId: SerializedItemId
          TargetLocation: PhysicalLocationTarget
          Notes: string option }

    let validateRelocationCommand
        (rawItemId: string)
        (req: RelocateItemRequestDto)
        : Result<ValidatedRelocationCommand, ApiError> =
        validation {
            let! itemId = SmartConstructors.createSerializedItemId "id" rawItemId
            and! targetLocation =
                SmartConstructors.createPhysicalLocationTarget req.ToLocationHolderId req.ToLocationCustomText

            let notes = SmartConstructors.createOptionalTrimmedString req.Notes

            return
                { ItemId = itemId
                  TargetLocation = targetLocation
                  Notes = notes }
        }
        |> Result.mapError ApiError.fromFieldErrors

    /// Loads the 20 most recent unique serialized items relocated in the organization.
    let loadRecentRelocatedItems (db: IDbContext) (orgId: OrgId) : RelocatedItemSummaryDto list =
        use conn = db.OpenReadConnection()

        conn
        |> Db.newCommand
            """
            WITH latest_logs AS (
                SELECT
                    l.serialized_item_id,
                    l.recorded_at,
                    l.admin_holder_id,
                    l.notes,
                    ROW_NUMBER() OVER (PARTITION BY l.serialized_item_id ORDER BY l.recorded_at DESC, l.id DESC) AS rn
                FROM serialized_item_location_logs l
                WHERE l.organization_id = @org_id
            )
            SELECT
                si.id AS serialized_item_id,
                si.item_type_id,
                it.name AS item_type_name,
                si.barcode,
                si.serial_number,
                si.status,
                si.current_holder_id,
                si.location_holder_id,
                si.location_custom_text,
                ll.recorded_at AS last_relocated_at,
                ll.admin_holder_id AS last_relocated_by_admin_id,
                ll.notes AS last_notes
            FROM latest_logs ll
            INNER JOIN serialized_items si ON si.id = ll.serialized_item_id
            INNER JOIN item_types it ON it.id = si.item_type_id
            WHERE ll.rn = 1
            ORDER BY ll.recorded_at DESC
            LIMIT 20
            """
        |> Db.setParams [ "org_id", SqlType.String(orgId.ToDbString()) ]
        |> Db.query (fun rd ->
            { SerializedItemId = rd.ReadString "serialized_item_id"
              ItemTypeId = rd.ReadString "item_type_id"
              ItemTypeName = rd.ReadString "item_type_name"
              Barcode = rd.ReadString "barcode"
              SerialNumber = rd.ReadStringOption "serial_number"
              Status = rd.ReadInt32 "status"
              CurrentHolderId = rd.ReadStringOption "current_holder_id"
              LocationHolderId = rd.ReadStringOption "location_holder_id"
              LocationCustomText = rd.ReadStringOption "location_custom_text"
              LastRelocatedAt = rd.ReadStringOption "last_relocated_at"
              LastRelocatedByAdminId = rd.ReadStringOption "last_relocated_by_admin_id"
              LastNotes = rd.ReadStringOption "last_notes" })

    let executeRelocationWorkflow
        (db: IDbContext)
        (auth: AuthenticatedContext)
        (rawItemId: string)
        (req: RelocateItemRequestDto)
        : Task<Result<RelocateItemResponseDto, ApiError>> =
        taskResult {
            let! cmd = validateRelocationCommand rawItemId req
            let orgIdStr = auth.ActiveOrg.OrganizationId.ToDbString()
            let itemIdStr = cmd.ItemId.ToDbString()
            let toHolderOpt, toCustomTextOpt = cmd.TargetLocation.ToDbColumns()

            return!
                db.ExecuteWriteTransactionAsync(fun conn tx ->
                    task {
                        let currentItemOpt =
                            conn
                            |> Db.newCommand
                                """
                                SELECT location_holder_id, location_custom_text
                                FROM serialized_items
                                WHERE id = @item_id AND organization_id = @org_id
                                """
                            |> Db.setTransaction tx
                            |> Db.setParams
                                [ "item_id", SqlType.String itemIdStr
                                  "org_id", SqlType.String orgIdStr ]
                            |> Db.querySingle (fun rd ->
                                rd.ReadStringOption "location_holder_id",
                                rd.ReadStringOption "location_custom_text")

                        match currentItemOpt with
                        | None ->
                            return Error(ApiError.withTarget ApiErrorCode.SerializedItemNotFound itemIdStr)
                        | Some(fromHolderOpt, fromTextOpt) ->
                            // Verify target holder (if any) belongs to the active organization and is active
                            let holderValidResult =
                                match toHolderOpt with
                                | None -> Ok()
                                | Some targetHolderId ->
                                    let holderStateOpt =
                                        conn
                                        |> Db.newCommand
                                            """
                                            SELECT is_active
                                            FROM custody_holders
                                            WHERE id = @holder_id AND organization_id = @org_id
                                            """
                                        |> Db.setTransaction tx
                                        |> Db.setParams
                                            [ "holder_id", SqlType.String targetHolderId
                                              "org_id", SqlType.String orgIdStr ]
                                        |> Db.querySingle (fun rd -> rd.ReadInt32 "is_active" = 1)

                                    match holderStateOpt with
                                    | None -> Error(ApiError.withTarget ApiErrorCode.HolderNotFound targetHolderId)
                                    | Some false -> Error(ApiError.withTarget ApiErrorCode.HolderInactive targetHolderId)
                                    | Some true -> Ok()

                            match holderValidResult with
                            | Error e -> return Error e
                            | Ok() ->
                                let nowIso = DateTimeOffset.UtcNow.ToString("O")
                                let logId = Guid.NewGuid().ToString("D")

                                conn
                                |> Db.newCommand
                                    """
                                    UPDATE serialized_items
                                    SET location_holder_id = @to_holder,
                                        location_custom_text = @to_text,
                                        updated_at = @now
                                    WHERE id = @item_id AND organization_id = @org_id
                                    """
                                |> Db.setTransaction tx
                                |> Db.setParams
                                    [ "to_holder",
                                      (match toHolderOpt with
                                       | Some h -> SqlType.String h
                                       | None -> SqlType.Null)
                                      "to_text",
                                      (match toCustomTextOpt with
                                       | Some t -> SqlType.String t
                                       | None -> SqlType.Null)
                                      "now", SqlType.String nowIso
                                      "item_id", SqlType.String itemIdStr
                                      "org_id", SqlType.String orgIdStr ]
                                |> Db.exec

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
                                      "item_id", SqlType.String itemIdStr
                                      "admin_id", SqlType.String(auth.ActiveOrg.HolderId.ToDbString())
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
                                      (match toCustomTextOpt with
                                       | Some t -> SqlType.String t
                                       | None -> SqlType.Null)
                                      "notes",
                                      (match cmd.Notes with
                                       | Some n -> SqlType.String n
                                       | None -> SqlType.Null)
                                      "now", SqlType.String nowIso ]
                                |> Db.exec

                                return
                                    Ok
                                        { LogId = logId
                                          SerializedItemId = itemIdStr
                                          ToLocationHolderId = toHolderOpt
                                          ToLocationCustomText = toCustomTextOpt
                                          RecordedAt = nowIso }
                    })
        }

    let getRelocateBootstrapHandler (db: IDbContext) (devMode: bool) : EndpointHandler =
        Http.runWorkflow (fun ctx ->
            taskResult {
                let! auth = Auth.requireAdmin db devMode ctx
                let! navbar = Navbar.buildNavbarAsync db ctx auth
                let recentItems = loadRecentRelocatedItems db auth.ActiveOrg.OrganizationId

                return
                    ({ Navbar = navbar
                       Page = { RecentRelocatedItems = recentItems } }
                    : Navbar.BootstrapEnvelopeDto<RelocatePageBootstrapDto>)
            })

    let queryLookupSerializedItemsHandler (db: IDbContext) (devMode: bool) : EndpointHandler =
        Http.runWorkflow (fun ctx ->
            taskResult {
                let! auth = Auth.requireAdmin db devMode ctx
                let! req = Http.bindJsonSafe<LookupSerializedItemsQueryDto> ctx

                let q =
                    SmartConstructors.createOptionalTrimmedString req.Query
                    |> Option.defaultValue ""

                use conn = db.OpenReadConnection()
                let pattern = "%" + q + "%"

                let matches =
                    conn
                    |> Db.newCommand
                        """
                        SELECT
                            si.id AS serialized_item_id,
                            si.item_type_id,
                            it.name AS item_type_name,
                            si.barcode,
                            si.serial_number,
                            si.status,
                            si.current_holder_id,
                            si.location_holder_id,
                            si.location_custom_text
                        FROM serialized_items si
                        INNER JOIN item_types it ON it.id = si.item_type_id
                        WHERE si.organization_id = @org_id
                          AND (
                              si.barcode LIKE @pattern
                              OR COALESCE(si.serial_number, '') LIKE @pattern
                              OR it.name LIKE @pattern
                          )
                        ORDER BY it.name ASC, si.barcode ASC
                        LIMIT 25
                        """
                    |> Db.setParams
                        [ "org_id", SqlType.String(auth.ActiveOrg.OrganizationId.ToDbString())
                          "pattern", SqlType.String pattern ]
                    |> Db.query (fun rd ->
                        { SerializedItemId = rd.ReadString "serialized_item_id"
                          ItemTypeId = rd.ReadString "item_type_id"
                          ItemTypeName = rd.ReadString "item_type_name"
                          Barcode = rd.ReadString "barcode"
                          SerialNumber = rd.ReadStringOption "serial_number"
                          Status = rd.ReadInt32 "status"
                          CurrentHolderId = rd.ReadStringOption "current_holder_id"
                          LocationHolderId = rd.ReadStringOption "location_holder_id"
                          LocationCustomText = rd.ReadStringOption "location_custom_text"
                          LastRelocatedAt = None
                          LastRelocatedByAdminId = None
                          LastNotes = None })

                return {| items = matches |}
            })

    let postRelocateItemHandler (db: IDbContext) (devMode: bool) (itemId: string) : EndpointHandler =
        Http.runWorkflow (fun ctx ->
            taskResult {
                let! auth = Auth.requireAdmin db devMode ctx
                let! req = Http.bindJsonSafe<RelocateItemRequestDto> ctx
                return! executeRelocationWorkflow db auth itemId req
            })

    let endpoints (db: IDbContext) (devMode: bool) : Endpoint list =
        [ GET [ route "/pages/items-relocate" (getRelocateBootstrapHandler db devMode) ]
          Http.QUERY_ROUTE "/serialized-items/lookup" (queryLookupSerializedItemsHandler db devMode)
          POST [ routef "/serialized-items/{%s}/relocate" (postRelocateItemHandler db devMode) ] ]
