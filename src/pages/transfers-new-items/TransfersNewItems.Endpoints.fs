namespace Rashmatz.Pages.TransfersNewItems

open Oxpecker
open FsToolkit.ErrorHandling
open Rashmatz.Shared
open Rashmatz.Shared.DomainTypes
open Rashmatz.Pages.TransfersNewItems.Domain

/// Step 6C Oxpecker HTTP Endpoints (`TransfersNewItems.Endpoints.fs`):
/// - `GET /pages/transfers-new-items?source_id=...&dest_id=...&template_id=...`
/// - `QUERY /serialized-items/check-unassigned`
/// - `POST /item-types`
/// - `POST /transfers` (supports `Idempotency-Key` header)
module Endpoints =

    let getTransfersNewItemsBootstrapHandler (db: IDbContext) (devMode: bool) : EndpointHandler =
        Http.runWorkflow (fun ctx ->
            taskResult {
                let! auth = Auth.requireAdmin db devMode ctx

                let rawSourceOpt =
                    match Http.tryGetQueryParam "source_id" ctx with
                    | Some "null" -> None
                    | other -> other

                let rawDestOpt =
                    match Http.tryGetQueryParam "dest_id" ctx with
                    | Some "null" -> None
                    | other -> other

                let rawTemplateOpt =
                    match Http.tryGetQueryParam "template_id" ctx with
                    | Some "null"
                    | Some "empty" -> None
                    | other -> other

                let! (sourceIdOpt, destIdOpt, templateIdOpt) =
                    validation {
                        let! s = SmartConstructors.createOptionalHolderId "source_id" rawSourceOpt
                        and! d = SmartConstructors.createOptionalHolderId "dest_id" rawDestOpt
                        and! t = SmartConstructors.createOptionalTemplateId "template_id" rawTemplateOpt
                        return (s, d, t)
                    }
                    |> Result.mapError ApiError.fromFieldErrors

                let! pageDto =
                    Db.loadStep6CBootstrapAsync
                        db
                        auth.ActiveOrg.OrganizationId
                        sourceIdOpt
                        destIdOpt
                        templateIdOpt

                let! navbar = Navbar.buildNavbarAsync db ctx auth

                return
                    ({ Navbar = navbar
                       Page = pageDto }
                    : Navbar.BootstrapEnvelopeDto<TransfersNewItemsPageDto>)
            })

    let queryCheckUnassignedHandler (db: IDbContext) (devMode: bool) : EndpointHandler =
        Http.runWorkflow (fun ctx ->
            taskResult {
                let! auth = Auth.requireAdmin db devMode ctx
                let! req = Http.bindJsonSafe<CheckUnassignedItemQueryDto> ctx
                return! Db.checkUnassignedItem db auth.ActiveOrg.OrganizationId req
            })

    let postCreateItemTypeHandler (db: IDbContext) (devMode: bool) : EndpointHandler =
        Http.runWorkflow (fun ctx ->
            taskResult {
                let! auth = Auth.requireAdmin db devMode ctx
                let! req = Http.bindJsonSafe<CreateItemTypeRequestDto> ctx
                let! validatedCmd = Domain.validateCreateItemType req
                return! Db.createItemTypeAsync db auth.ActiveOrg.OrganizationId validatedCmd
            })

    let postCommitTransferHandler (db: IDbContext) (devMode: bool) : EndpointHandler =
        Http.runWorkflow (fun ctx ->
            taskResult {
                let! auth = Auth.requireAdmin db devMode ctx
                let idempotencyKeyOpt = Http.tryGetHeader "Idempotency-Key" ctx
                let! req = Http.bindJsonSafe<CommitTransferRequestDto> ctx
                let! validatedCmd = Domain.validateCommitTransfer req
                return! Db.commitTransferAsync db auth idempotencyKeyOpt validatedCmd
            })

    let endpoints (db: IDbContext) (devMode: bool) : Endpoint list =
        [ GET [ route "/pages/transfers-new-items" (getTransfersNewItemsBootstrapHandler db devMode) ]
          Http.QUERY_ROUTE "/serialized-items/check-unassigned" (queryCheckUnassignedHandler db devMode)
          POST
              [ route "/item-types" (postCreateItemTypeHandler db devMode)
                route "/transfers" (postCommitTransferHandler db devMode) ] ]
