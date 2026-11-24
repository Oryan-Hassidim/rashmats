namespace Rashmatz.Pages.TransfersNewParties

open Oxpecker
open Oxpecker.OpenApi
open Donald
open FsToolkit.ErrorHandling
open Rashmatz.Shared
open Rashmatz.Pages.TransfersNewTemplate.Server

/// Vertical Slice — Page 6B: Select Source & Destination Parties (`/transfers/new/parties`)
module Server =

    type TransfersNewPartiesPage = { SelectedTemplate: TemplateHeader option }

    let private loadTemplate (db: IDbContext) (orgId: OrgId) (tplIdOpt: TemplateId option) =
        match tplIdOpt with
        | None -> Ok None
        | Some tplId ->
            use conn = db.OpenRead()
            conn
            |> Db.newCommand "SELECT id, name, description, default_source_holder_id, default_dest_holder_id FROM transfer_templates WHERE organization_id=@org AND id=@id AND is_active=1"
            |> Db.setParams [ "org", SqlType.String orgId.Str; "id", SqlType.String tplId.Str ]
            |> Db.querySingle (fun rd ->
                { Id = rd.ReadString "id"; Name = rd.ReadString "name"; Description = rd.ReadStringOption "description"
                  DefaultSourceHolderId = rd.ReadStringOption "default_source_holder_id"
                  DefaultDestHolderId = rd.ReadStringOption "default_dest_holder_id" })
            |> function
                | Some t -> Ok(Some t)
                | None -> Error(ApiError.withTarget ApiErrorCode.TemplateNotFound tplId.Str)

    let endpoints (db: IDbContext) (devMode: bool) : Endpoint list =
        [ GET [
            route "/pages/transfers-new-parties" (Http.runWorkflow (fun ctx ->
                taskResult {
                    let! auth = Auth.requireAdmin db devMode ctx
                    let! tplId = Parse.optTemplateId "template_id" (Http.queryParam "template_id" ctx) |> Result.mapError (fun e -> ApiError.fromFields [ e ])
                    let! selected = loadTemplate db auth.ActiveOrg.OrgId tplId
                    return ({ Navbar = Navbar.build db ctx auth; Page = { SelectedTemplate = selected } } : Navbar.PageEnvelope<TransfersNewPartiesPage>)
                }))
            |> addOpenApiSimple<unit, Navbar.PageEnvelope<TransfersNewPartiesPage>>
          ] ]
