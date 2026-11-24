namespace Rashmatz.Shared

open System
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Oxpecker

/// Oxpecker HTTP helpers connecting Task<Result<'T, ApiError>> to ASP.NET Core responses
/// using global SnakeCaseLower JSON serialization.
module Http =

    let snakeCaseJsonOptions =
        JsonSerializerOptions(
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true
        )

    let runWorkflow (workflow: HttpContext -> Task<Result<'T, ApiError>>) : EndpointHandler =
        fun ctx ->
            task {
                match! workflow ctx with
                | Ok payload ->
                    ctx.Response.StatusCode <- 200
                    do! ctx.WriteJson payload
                | Error err ->
                    let dto = ApiError.toDto err
                    ctx.Response.StatusCode <- dto.Status
                    do! ctx.WriteJson dto
            }

    let bindJson<'T> (ctx: HttpContext) : Task<Result<'T, ApiError>> =
        task {
            try
                let! model = ctx.BindJson<'T>()
                if isNull (box model) then return Error(ApiError.single ApiErrorCode.ValidationFailed)
                else return Ok model
            with _ ->
                return Error(ApiError.single ApiErrorCode.ValidationFailed)
        }

    let queryParam (key: string) (ctx: HttpContext) =
        ctx.TryGetQueryValue key |> Option.bind (fun s -> trimNullable s)

    let header (key: string) (ctx: HttpContext) =
        match ctx.Request.Headers.TryGetValue key with
        | true, v when v.Count > 0 -> trimNullable v[0]
        | _ -> None

    /// Registers an HTTP `QUERY` endpoint on Oxpecker's standard `route`.
    let queryRoute (path: string) (handler: EndpointHandler) : Endpoint =
        route path (fun ctx ->
            if String.Equals(ctx.Request.Method, "QUERY", StringComparison.OrdinalIgnoreCase) then handler ctx
            else ctx.Response.StatusCode <- StatusCodes.Status405MethodNotAllowed; Task.CompletedTask)
