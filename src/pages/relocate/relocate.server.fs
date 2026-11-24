namespace Rashmatz.Pages.Relocate

open System
open Oxpecker
open Oxpecker.OpenApi
open Donald
open FsToolkit.ErrorHandling
open Rashmatz.Shared

/// Vertical Slice — Page 5: Quick Physical Relocation (`/items/relocate`)
module Server =

    type RelocatedItemSummary =
        { SerializedItemId: string; ItemTypeId: string; ItemTypeName: string; Barcode: string
          SerialNumber: string option; Status: int; CurrentHolderId: string option
          LocationHolderId: string option; LocationCustomText: string option
          LastRelocatedAt: string option; LastRelocatedByAdminId: string option; LastNotes: string option }

    type RelocatePage = { RecentRelocatedItems: RelocatedItemSummary list }
    [<CLIMutable>] type LookupQuery = { Q: string option }
    type LookupResponse = { Items: RelocatedItemSummary list }
    [<CLIMutable>] type RelocateRequest = { ToLocationHolderId: string option; ToLocationCustomText: string option; Notes: string option }
    type RelocateResponse = { LogId: string; SerializedItemId: string; ToLocationHolderId: string option; ToLocationCustomText: string option; RecordedAt: string }

    let private readSummary (withLog: bool) (rd: System.Data.IDataReader) =
        { SerializedItemId = rd.ReadString "serialized_item_id"; ItemTypeId = rd.ReadString "item_type_id"
          ItemTypeName = rd.ReadString "item_type_name"; Barcode = rd.ReadString "barcode"
          SerialNumber = rd.ReadStringOption "serial_number"; Status = rd.ReadInt32 "status"
          CurrentHolderId = rd.ReadStringOption "current_holder_id"; LocationHolderId = rd.ReadStringOption "location_holder_id"
          LocationCustomText = rd.ReadStringOption "location_custom_text"
          LastRelocatedAt = if withLog then rd.ReadStringOption "last_relocated_at" else None
          LastRelocatedByAdminId = if withLog then rd.ReadStringOption "last_relocated_by_admin_id" else None
          LastNotes = if withLog then rd.ReadStringOption "last_notes" else None }

    let private executeRelocation (db: IDbContext) (auth: AuthContext) (rawItemId: string) (req: RelocateRequest) =
        taskResult {
            let! itemId, target =
                validation {
                    let! id = Parse.serializedItemId "id" rawItemId
                    and! loc = Parse.location req.ToLocationHolderId req.ToLocationCustomText
                    return id, loc
                }
                |> Result.mapError ApiError.fromFields

            let orgId, toHolder, toText, notes = auth.ActiveOrg.OrgId.Str, fst (target.ToDbCols()), snd (target.ToDbCols()), trimOpt req.Notes

            return! db.WriteTxAsync(fun conn tx ->
                task {
                    let current =
                        conn |> Db.newCommand "SELECT location_holder_id, location_custom_text FROM serialized_items WHERE id=@id AND organization_id=@org"
                        |> Db.setTransaction tx |> Db.setParams [ "id", SqlType.String itemId.Str; "org", SqlType.String orgId ]
                        |> Db.querySingle (fun rd -> rd.ReadStringOption "location_holder_id", rd.ReadStringOption "location_custom_text")

                    match current with
                    | None -> return Error(ApiError.withTarget ApiErrorCode.SerializedItemNotFound itemId.Str)
                    | Some(fromHolder, fromText) ->
                        let holderCheck =
                            match toHolder with
                            | None -> Ok()
                            | Some hid ->
                                match conn |> Db.newCommand "SELECT is_active FROM custody_holders WHERE id=@id AND organization_id=@org"
                                      |> Db.setTransaction tx |> Db.setParams [ "id", SqlType.String hid; "org", SqlType.String orgId ]
                                      |> Db.querySingle (fun rd -> rd.ReadInt32 "is_active" = 1) with
                                | None -> Error(ApiError.withTarget ApiErrorCode.HolderNotFound hid)
                                | Some false -> Error(ApiError.withTarget ApiErrorCode.HolderInactive hid)
                                | Some true -> Ok()

                        match holderCheck with
                        | Error e -> return Error e
                        | Ok() ->
                            let now, logId = DateTimeOffset.UtcNow.ToString("O"), Guid.NewGuid().ToString("D")
                            conn |> Db.newCommand "UPDATE serialized_items SET location_holder_id=@th, location_custom_text=@tt, updated_at=@now WHERE id=@id AND organization_id=@org"
                            |> Db.setTransaction tx |> Db.setParams [ "th", Sql.strOpt toHolder; "tt", Sql.strOpt toText; "now", SqlType.String now; "id", SqlType.String itemId.Str; "org", SqlType.String orgId ] |> Db.exec

                            conn |> Db.newCommand "INSERT INTO serialized_item_location_logs (id, organization_id, serialized_item_id, admin_holder_id, from_location_holder_id, from_location_custom_text, to_location_holder_id, to_location_custom_text, notes, recorded_at) VALUES (@id, @org, @item, @adm, @fh, @ft, @th, @tt, @notes, @now)"
                            |> Db.setTransaction tx |> Db.setParams [ "id", SqlType.String logId; "org", SqlType.String orgId; "item", SqlType.String itemId.Str; "adm", SqlType.String auth.ActiveOrg.HolderId.Str; "fh", Sql.strOpt fromHolder; "ft", Sql.strOpt fromText; "th", Sql.strOpt toHolder; "tt", Sql.strOpt toText; "notes", Sql.strOpt notes; "now", SqlType.String now ] |> Db.exec

                            return Ok { LogId = logId; SerializedItemId = itemId.Str; ToLocationHolderId = toHolder; ToLocationCustomText = toText; RecordedAt = now }
                })
        }

    let endpoints (db: IDbContext) (devMode: bool) : Endpoint list =
        [ GET [
            route "/pages/items-relocate" (Http.runWorkflow (fun ctx ->
                taskResult {
                    let! auth = Auth.requireAdmin db devMode ctx
                    use conn = db.OpenRead()
                    let recent =
                        conn |> Db.newCommand (Sql.load "pages/relocate/recent_relocations.sql")
                        |> Db.setParams [ "org_id", SqlType.String auth.ActiveOrg.OrgId.Str ]
                        |> Db.query (readSummary true)
                    return ({ Navbar = Navbar.build db ctx auth; Page = { RecentRelocatedItems = recent } } : Navbar.PageEnvelope<RelocatePage>)
                }))
            |> addOpenApiSimple<unit, Navbar.PageEnvelope<RelocatePage>>
          ]
          Http.queryRoute "/serialized-items/lookup" (Http.runWorkflow (fun ctx ->
              taskResult {
                  let! auth = Auth.requireAdmin db devMode ctx
                  let! req = Http.bindJson<LookupQuery> ctx
                  let pattern = "%" + (trimOpt req.Q |> Option.defaultValue "") + "%"
                  use conn = db.OpenRead()
                  let items =
                      conn
                      |> Db.newCommand "SELECT si.id AS serialized_item_id, si.item_type_id, it.name AS item_type_name, si.barcode, si.serial_number, si.status, si.current_holder_id, si.location_holder_id, si.location_custom_text FROM serialized_items si INNER JOIN item_types it ON it.id=si.item_type_id WHERE si.organization_id=@org AND (si.barcode LIKE @p OR COALESCE(si.serial_number,'') LIKE @p OR it.name LIKE @p) ORDER BY it.name, si.barcode LIMIT 25"
                      |> Db.setParams [ "org", SqlType.String auth.ActiveOrg.OrgId.Str; "p", SqlType.String pattern ]
                      |> Db.query (readSummary false)
                  return { Items = items }
              }))
          POST [
            routef "/serialized-items/{%s}/relocate" (fun itemId ->
                Http.runWorkflow (fun ctx ->
                    taskResult {
                        let! auth = Auth.requireAdmin db devMode ctx
                        let! req = Http.bindJson<RelocateRequest> ctx
                        return! executeRelocation db auth itemId req
                    }))
            |> addOpenApiSimple<RelocateRequest, RelocateResponse>
          ] ]
