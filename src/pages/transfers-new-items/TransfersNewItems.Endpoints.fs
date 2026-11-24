namespace Rashmatz.Pages.TransfersNewItems

open Oxpecker
open Oxpecker.OpenApi
open FsToolkit.ErrorHandling
open Rashmatz.Shared
open Rashmatz.Pages.TransfersNewItems.Domain

/// Step 6C Oxpecker Endpoints — clean, concise, and documented for OpenAPI.
module Endpoints =

    let private cleanParam name ctx =
        match Http.queryParam name ctx with
        | Some "null" | Some "empty" -> None
        | other -> other

    let endpoints (db: IDbContext) (devMode: bool) : Endpoint list =
        [ GET [
            route "/pages/transfers-new-items" (Http.runWorkflow (fun ctx ->
                taskResult {
                    let! auth = Auth.requireAdmin db devMode ctx
                    let! src, dst, tpl =
                        validation {
                            let! s = Parse.optHolderId "source_id" (cleanParam "source_id" ctx)
                            and! d = Parse.optHolderId "dest_id" (cleanParam "dest_id" ctx)
                            and! t = Parse.optTemplateId "template_id" (cleanParam "template_id" ctx)
                            return s, d, t
                        }
                        |> Result.mapError ApiError.fromFields

                    let! page = Db.loadBootstrap db auth.ActiveOrg.OrgId src dst tpl
                    return ({ Navbar = Navbar.build db ctx auth; Page = page } : Navbar.PageEnvelope<TransfersNewItemsPage>)
                }))
            |> addOpenApiSimple<unit, Navbar.PageEnvelope<TransfersNewItemsPage>>
          ]
          Http.queryRoute "/serialized-items/check-unassigned" (Http.runWorkflow (fun ctx ->
              taskResult {
                  let! auth = Auth.requireAdmin db devMode ctx
                  let! req = Http.bindJson<CheckUnassignedQuery> ctx
                  return! Db.checkUnassigned db auth.ActiveOrg.OrgId req
              }))
          POST [
            route "/item-types" (Http.runWorkflow (fun ctx ->
                taskResult {
                    let! auth = Auth.requireAdmin db devMode ctx
                    let! req = Http.bindJson<CreateItemTypeRequest> ctx
                    let! cmd = Domain.validateCreateItemType req
                    return! Db.createItemType db auth.ActiveOrg.OrgId cmd
                }))
            |> addOpenApiSimple<CreateItemTypeRequest, CatalogItemType>

            route "/transfers" (Http.runWorkflow (fun ctx ->
                taskResult {
                    let! auth = Auth.requireAdmin db devMode ctx
                    let! req = Http.bindJson<CommitTransferRequest> ctx
                    let! cmd = Domain.validateCommitTransfer req
                    return! Db.commitTransfer db auth (Http.header "Idempotency-Key" ctx) cmd
                }))
            |> addOpenApiSimple<CommitTransferRequest, CommitTransferResponse>
          ] ]
