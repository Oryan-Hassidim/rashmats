namespace Rashmatz.Pages.TransfersNewTemplate

open System.Text.Json.Serialization
open System.Threading.Tasks
open Oxpecker
open Donald
open FsToolkit.ErrorHandling
open Rashmatz.Shared
open Rashmatz.Shared.Schema

/// Vertical Slice for Page 6A: Step 1 - Choosing a Transfer Template or Free Transfer (`/transfers/new/template`)
/// Co-located inside `src/pages/transfers-new-template/transfers-new-template.server.fs`.
/// Returns active template headers ONLY (without template line items) to minimize payload size.
module Server =

    type TransferTemplateHeaderDto =
        { [<JsonPropertyName("id")>]
          Id: string
          [<JsonPropertyName("name")>]
          Name: string
          [<JsonPropertyName("description")>]
          Description: string option
          [<JsonPropertyName("default_source_holder_id")>]
          DefaultSourceHolderId: string option
          [<JsonPropertyName("default_dest_holder_id")>]
          DefaultDestHolderId: string option }

    type TransfersNewTemplatePageDto =
        { [<JsonPropertyName("templates")>]
          Templates: TransferTemplateHeaderDto list }

    let loadTemplateHeadersAsync
        (db: IDbContext)
        (auth: AuthenticatedContext)
        : Task<TransferTemplateHeaderDto list> =
        task {
            use conn = db.OpenReadConnection()
            let orgIdStr = auth.ActiveOrg.OrganizationId.ToDbString()

            let rows =
                conn
                |> Db.newCommand
                    """
                    SELECT id, organization_id, name, description, default_source_holder_id, default_dest_holder_id, is_active, created_at, updated_at
                    FROM transfer_templates
                    WHERE organization_id = @org_id AND is_active = 1
                    ORDER BY name ASC
                    """
                |> Db.setParams [ "org_id", SqlType.String orgIdStr ]
                |> Db.query main.Readers.read_transfer_templates

            return
                rows
                |> List.map (fun (t: main.transfer_templates) ->
                    { Id = t.id
                      Name = t.name
                      Description = t.description
                      DefaultSourceHolderId = t.default_source_holder_id
                      DefaultDestHolderId = t.default_dest_holder_id })
        }

    let getTransfersNewTemplateBootstrapHandler (db: IDbContext) (devMode: bool) : EndpointHandler =
        Http.runWorkflow (fun ctx ->
            taskResult {
                let! auth = Auth.requireAdmin db devMode ctx
                let! navbar = Navbar.buildNavbarAsync db ctx auth
                let! templates = loadTemplateHeadersAsync db auth

                return
                    ({ Navbar = navbar
                       Page = { Templates = templates } }
                    : Navbar.BootstrapEnvelopeDto<TransfersNewTemplatePageDto>)
            })

    let endpoints (db: IDbContext) (devMode: bool) : Endpoint list =
        [ GET [ route "/pages/transfers-new-template" (getTransfersNewTemplateBootstrapHandler db devMode) ] ]
