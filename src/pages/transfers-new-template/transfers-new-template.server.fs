namespace Rashmatz.Pages.TransfersNewTemplate

open Oxpecker
open Oxpecker.OpenApi
open Donald
open FsToolkit.ErrorHandling
open Rashmatz.Shared

/// Vertical Slice — Page 6A: Choose Transfer Template or Free Transfer (`/transfers/new/template`)
module Server =

    type TemplateHeader =
        { Id: string; Name: string; Description: string option
          DefaultSourceHolderId: string option; DefaultDestHolderId: string option }

    type TransfersNewTemplatePage = { Templates: TemplateHeader list }

    let endpoints (db: IDbContext) (devMode: bool) : Endpoint list =
        [ GET [
            route "/pages/transfers-new-template" (Http.runWorkflow (fun ctx ->
                taskResult {
                    let! auth = Auth.requireAdmin db devMode ctx
                    use conn = db.OpenRead()
                    let templates =
                        conn
                        |> Db.newCommand "SELECT id, name, description, default_source_holder_id, default_dest_holder_id FROM transfer_templates WHERE organization_id=@org AND is_active=1 ORDER BY name"
                        |> Db.setParams [ "org", SqlType.String auth.ActiveOrg.OrgId.Str ]
                        |> Db.query (fun rd ->
                            { Id = rd.ReadString "id"; Name = rd.ReadString "name"; Description = rd.ReadStringOption "description"
                              DefaultSourceHolderId = rd.ReadStringOption "default_source_holder_id"
                              DefaultDestHolderId = rd.ReadStringOption "default_dest_holder_id" })
                    return ({ Navbar = Navbar.build db ctx auth; Page = { Templates = templates } } : Navbar.PageEnvelope<TransfersNewTemplatePage>)
                }))
            |> addOpenApiSimple<unit, Navbar.PageEnvelope<TransfersNewTemplatePage>>
          ] ]
