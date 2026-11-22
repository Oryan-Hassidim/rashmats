namespace Rashmatz.Pages.TransfersNewParties

open System.Text.Json.Serialization
open System.Threading.Tasks
open Oxpecker
open Donald
open FsToolkit.ErrorHandling
open Rashmatz.Shared
open Rashmatz.Shared.DomainTypes
open Rashmatz.Shared.Schema
open Rashmatz.Pages.TransfersNewTemplate.Server

/// Vertical Slice for Page 6B: Step 2 - Selecting Transfer Parties (`/transfers/new/parties`)
/// Co-located inside `src/pages/transfers-new-parties/transfers-new-parties.server.fs`.
/// Relies on the organization's `holders_directory` cached in the client via `X-Holders-Hash`,
/// and if `?template_id=<uuid>` was provided, returns the selected template header so the client
/// automatically pre-populates `default_source_holder_id` and `default_dest_holder_id`.
module Server =

    type TransfersNewPartiesPageDto =
        { [<JsonPropertyName("selected_template")>]
          SelectedTemplate: TransferTemplateHeaderDto option }

    let loadSelectedTemplateHeaderAsync
        (db: IDbContext)
        (orgId: OrgId)
        (templateIdOpt: TemplateId option)
        : Task<Result<TransferTemplateHeaderDto option, ApiError>> =
        task {
            match templateIdOpt with
            | None -> return Ok None
            | Some templateId ->
                use conn = db.OpenReadConnection()
                let orgIdStr = orgId.ToDbString()
                let tplIdStr = templateId.ToDbString()

                let rowOpt =
                    conn
                    |> Db.newCommand
                        """
                        SELECT id, organization_id, name, description, default_source_holder_id, default_dest_holder_id, is_active, created_at, updated_at
                        FROM transfer_templates
                        WHERE organization_id = @org_id AND id = @id AND is_active = 1
                        LIMIT 1
                        """
                    |> Db.setParams
                        [ "org_id", SqlType.String orgIdStr
                          "id", SqlType.String tplIdStr ]
                    |> Db.querySingle main.Readers.read_transfer_templates

                match rowOpt with
                | None -> return Error(ApiError.withTarget ApiErrorCode.TemplateNotFound tplIdStr)
                | Some(t: main.transfer_templates) ->
                    return
                        Ok(
                            Some
                                { Id = t.id
                                  Name = t.name
                                  Description = t.description
                                  DefaultSourceHolderId = t.default_source_holder_id
                                  DefaultDestHolderId = t.default_dest_holder_id }
                        )
        }

    let getTransfersNewPartiesBootstrapHandler (db: IDbContext) (devMode: bool) : EndpointHandler =
        Http.runWorkflow (fun ctx ->
            taskResult {
                let! auth = Auth.requireAdmin db devMode ctx
                let rawTemplateIdOpt = Http.tryGetQueryParam "template_id" ctx

                let! templateIdOpt =
                    SmartConstructors.createOptionalTemplateId "template_id" rawTemplateIdOpt
                    |> Result.mapError (fun e -> ApiError.fromFieldErrors [ e ])

                let! selectedTemplateOpt =
                    loadSelectedTemplateHeaderAsync db auth.ActiveOrg.OrganizationId templateIdOpt

                let! navbar = Navbar.buildNavbarAsync db ctx auth

                return
                    ({ Navbar = navbar
                       Page = { SelectedTemplate = selectedTemplateOpt } }
                    : Navbar.BootstrapEnvelopeDto<TransfersNewPartiesPageDto>)
            })

    let endpoints (db: IDbContext) (devMode: bool) : Endpoint list =
        [ GET [ route "/pages/transfers-new-parties" (getTransfersNewPartiesBootstrapHandler db devMode) ] ]
