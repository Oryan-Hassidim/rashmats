namespace Rashmatz

open System
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Oxpecker
open Oxpecker.OpenApi
open FsToolkit.ErrorHandling
open Rashmatz.Shared

type ProductionGoogleTokenValidator(httpClient: HttpClient) =
    interface IGoogleTokenValidator with
        member _.ValidateAsync(idToken: string) =
            task {
                if String.IsNullOrWhiteSpace idToken then
                    return Error(ApiError.single ApiErrorCode.InvalidGoogleToken)
                else
                    try
                        let! resp =
                            httpClient.GetAsync(
                                "https://oauth2.googleapis.com/tokeninfo?id_token="
                                + Uri.EscapeDataString idToken
                            )

                        if not resp.IsSuccessStatusCode then
                            return Error(ApiError.single ApiErrorCode.InvalidGoogleToken)
                        else
                            use! stream = resp.Content.ReadAsStreamAsync()
                            use! doc = JsonDocument.ParseAsync stream
                            let root = doc.RootElement

                            match trimNullable (root.GetProperty("sub").GetString()),
                                  trimNullable (root.GetProperty("email").GetString())
                                with
                            | Some sub, Some email ->
                                let name =
                                    match root.TryGetProperty("name") with
                                    | true, p ->
                                        trimNullable (p.GetString())
                                        |> Option.defaultValue email
                                    | _ -> email

                                let pic =
                                    match root.TryGetProperty("picture") with
                                    | true, p -> trimNullable (p.GetString())
                                    | _ -> None

                                return
                                    Ok
                                        { Subject = sub
                                          Email = email.ToLowerInvariant()
                                          FullName = name
                                          AvatarUrl = pic }
                            | _ -> return Error(ApiError.single ApiErrorCode.InvalidGoogleToken)
                    with
                    | _ -> return Error(ApiError.single ApiErrorCode.InvalidGoogleToken)
            }

module App =

    let registerServices (services: IServiceCollection) =
        services.AddRouting() |> ignore
        services.AddOxpecker() |> ignore

        services.AddSingleton<IJsonSerializer>(SystemTextJsonSerializer Http.snakeCaseJsonOptions)
        |> ignore

        services.ConfigureHttpJsonOptions (fun o ->
            o.SerializerOptions.PropertyNamingPolicy <- JsonNamingPolicy.SnakeCaseLower
            o.SerializerOptions.PropertyNameCaseInsensitive <- true)
        |> ignore

        services.AddOpenApi (fun o ->
            o.AddSchemaTransformer<FSharpOptionSchemaTransformer>()
            |> ignore)
        |> ignore

    let buildEndpoints (db: IDbContext) (googleValidator: IGoogleTokenValidator) (devMode: bool) : Endpoint list =
        [ POST [ route
                     "/organizations"
                     (Http.runWorkflow (fun ctx ->
                         taskResult {
                             let! auth = Auth.requireSuperAdmin db devMode ctx
                             let! req = Http.bindJson<Navbar.CreateOrgRequest> ctx
                             return! Navbar.createOrganization db auth req
                         }))
                 |> Http.openApiPost<Navbar.CreateOrgRequest, Navbar.CreateOrgResponse> [ 401; 403; 409; 422 ]

                 route
                     "/dev/seed"
                     (Http.runWorkflow (fun _ ->
                         taskResult {
                             if not devMode then
                                 return! Error(ApiError.single ApiErrorCode.ForbiddenRole)
                             else
                                 return Database.ensureDevSeed db
                         }))
                 |> Http.openApiPost<unit, DevSeedSummary> [ 403 ] ]
          yield! Rashmatz.Pages.Login.Server.endpoints db googleValidator devMode
          yield! Rashmatz.Pages.MyCustody.Server.endpoints db devMode
          yield! Rashmatz.Pages.Relocate.Server.endpoints db devMode
          yield! Rashmatz.Pages.TransfersNewTemplate.Server.endpoints db devMode
          yield! Rashmatz.Pages.TransfersNewParties.Server.endpoints db devMode
          yield! Rashmatz.Pages.TransfersNewItems.Endpoints.endpoints db devMode ]

    let configure (app: WebApplication) (db: IDbContext) (googleValidator: IGoogleTokenValidator) (devMode: bool) =
        app.MapOpenApi() |> ignore

        app
            .UseRouting()
            .UseOxpecker(buildEndpoints db googleValidator devMode)
        |> ignore

module Program =

    [<EntryPoint>]
    let main args =
        let builder = WebApplication.CreateBuilder(args)

        let connStr =
            trimNullable builder.Configuration["ConnectionStrings:Sqlite"]
            |> Option.defaultValue "Data Source=rashmatz.db"

        let db = Database.SqliteDbContext(connStr) :> IDbContext

        if args |> Array.contains "--init-db" then
            printfn "[Rashmatz] SQLite schema initialized (%s)." connStr
            0
        elif args |> Array.contains "--seed-db" then
            let s = Database.ensureDevSeed db

            printfn
                "[Rashmatz] DB seeded (%s) | Org: %s | Admin: %s | Member: %s"
                connStr
                s.OrganizationSlug
                s.AdminEmail
                s.MemberEmail

            0
        else
            let devMode =
                builder.Environment.IsDevelopment()
                || (trimNullable builder.Configuration["Auth:EnableDevMode"]
                    |> Option.exists (fun v -> v.Equals("true", StringComparison.OrdinalIgnoreCase)))

            if devMode then
                let s = Database.ensureDevSeed db

                printfn
                    "[Rashmatz Dev] OpenAPI: /openapi/v1.json | Org: %s | Admin: %s | Member: %s"
                    s.OrganizationSlug
                    s.AdminEmail
                    s.MemberEmail

            builder.Services.AddSingleton<IDbContext>(db)
            |> ignore

            builder.Services.AddHttpClient() |> ignore

            builder.Services.AddSingleton<IGoogleTokenValidator> (fun sp ->
                ProductionGoogleTokenValidator(
                    sp
                        .GetRequiredService<IHttpClientFactory>()
                        .CreateClient()
                )
                :> IGoogleTokenValidator)
            |> ignore

            App.registerServices builder.Services

            let app = builder.Build()
            App.configure app db (app.Services.GetRequiredService<IGoogleTokenValidator>()) devMode
            app.Run()
            0
