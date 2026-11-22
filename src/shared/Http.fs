namespace Rashmatz.Shared

open System
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Oxpecker
open Rashmatz.Shared.DomainTypes

/// Oxpecker HTTP helpers bridging Railway Oriented Programming (`Task<Result<'T, ApiError>>`)
/// to ASP.NET Core EndpointRouting responses, including support for the HTTP `QUERY` verb.
module Http =

    let jsonOptions =
        let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
        opts

    let writeResult (ctx: HttpContext) (outcome: Result<'T, ApiError>) : Task =
        task {
            match outcome with
            | Ok payload ->
                ctx.Response.StatusCode <- 200
                do! ctx.WriteJson payload
            | Error apiErr ->
                let errDto = ApiError.toDto apiErr
                ctx.Response.StatusCode <- errDto.Status
                do! ctx.WriteJson errDto
        }

    let runWorkflow (workflow: HttpContext -> Task<Result<'T, ApiError>>) : EndpointHandler =
        fun (ctx: HttpContext) ->
            task {
                let! outcome = workflow ctx
                do! writeResult ctx outcome
            }

    let bindJsonSafe<'T> (ctx: HttpContext) : Task<Result<'T, ApiError>> =
        task {
            try
                let! model = ctx.BindJson<'T>()

                if isNull (box model) then
                    return Error(ApiError.single ApiErrorCode.ValidationFailed)
                else
                    return Ok model
            with _ ->
                return Error(ApiError.single ApiErrorCode.ValidationFailed)
        }

    let tryGetQueryParam (key: string) (ctx: HttpContext) : string option =
        match ctx.TryGetQueryValue(key) with
        | Some v when not (String.IsNullOrWhiteSpace(v)) -> Some(v.Trim())
        | _ -> None

    let tryGetHeader (key: string) (ctx: HttpContext) : string option =
        match ctx.Request.Headers.TryGetValue(key) with
        | true, v when v.Count > 0 -> ofNullableTrimmed v[0]
        | _ -> None

    /// Registers an HTTP `QUERY` endpoint using Oxpecker's standard `route` primitive,
    /// verifying that the incoming request verb is `QUERY`.
    let QUERY_ROUTE (path: string) (handler: EndpointHandler) : Endpoint =
        route path (fun (ctx: HttpContext) ->
            if String.Equals(ctx.Request.Method, "QUERY", StringComparison.OrdinalIgnoreCase) then
                handler ctx
            else
                ctx.Response.StatusCode <- StatusCodes.Status405MethodNotAllowed
                Task.CompletedTask)
