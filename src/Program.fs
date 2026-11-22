namespace Rashmatz

open System
open System.Net.Http
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Oxpecker
open FsToolkit.ErrorHandling
open Rashmatz.Shared
open Rashmatz.Shared.DomainTypes

/// Default production Google Token Validator (can be replaced in DI by any mock IGoogleTokenValidator in tests).
type ProductionGoogleTokenValidator(httpClient: HttpClient) =
    interface IGoogleTokenValidator with
        member _.ValidateIdTokenAsync(idToken: string) : Task<Result<GoogleUserPayload, ApiError>> =
            task {
                if String.IsNullOrWhiteSpace(idToken) then
                    return Error(ApiError.single ApiErrorCode.InvalidGoogleToken)
                else
                    try
                        let url =
                            "https://oauth2.googleapis.com/tokeninfo?id_token="
                            + Uri.EscapeDataString(idToken)

                        let! response = httpClient.GetAsync(url)

                        if not response.IsSuccessStatusCode then
                            return Error(ApiError.single ApiErrorCode.InvalidGoogleToken)
                        else
                            use! stream = response.Content.ReadAsStreamAsync()
                            use! doc = System.Text.Json.JsonDocument.ParseAsync(stream)
                            let root = doc.RootElement

                            let subOpt = ofNullableTrimmed (root.GetProperty("sub").GetString())
                            let emailOpt = ofNullableTrimmed (root.GetProperty("email").GetString())

                            match subOpt, emailOpt with
                            | Some sub, Some email ->
                                let name =
                                    match root.TryGetProperty("name") with
                                    | true, p -> ofNullableTrimmed (p.GetString()) |> Option.defaultValue email
                                    | false, _ -> email

                                let pictureOpt =
                                    match root.TryGetProperty("picture") with
                                    | true, p -> ofNullableTrimmed (p.GetString())
                                    | false, _ -> None

                                return
                                    Ok
                                        { GoogleSubject = sub
                                          Email = email.ToLowerInvariant()
                                          FullName = name
                                          AvatarUrl = pictureOpt }
                            | _ -> return Error(ApiError.single ApiErrorCode.InvalidGoogleToken)
                    with _ ->
                        return Error(ApiError.single ApiErrorCode.InvalidGoogleToken)
            }

module AppRoutes =

    let postCreateOrganizationHandler (db: IDbContext) (devMode: bool) : EndpointHandler =
        Http.runWorkflow (fun ctx ->
            taskResult {
                let! auth = Auth.requireSuperAdmin db devMode ctx
                let! req = Http.bindJsonSafe<Navbar.CreateOrganizationRequestDto> ctx
                return! Navbar.createOrganizationWorkflow db auth req
            })

    let postDevSeedHandler (db: IDbContext) (devMode: bool) : EndpointHandler =
        Http.runWorkflow (fun _ctx ->
            taskResult {
                if not devMode then
                    return! Error(ApiError.single ApiErrorCode.ForbiddenRole)
                else
                    return Database.ensureDevelopmentSeed db
            })

    /// Aggregates all Vertical Slice endpoints into a single Oxpecker Endpoint routing tree.
    let buildEndpoints
        (db: IDbContext)
        (googleValidator: IGoogleTokenValidator)
        (devModeEnabled: bool)
        : Endpoint list =
        [ // Shared Global Shell / Super-Admin Organization Creation & Dev DB Seeding
          POST
              [ route "/organizations" (postCreateOrganizationHandler db devModeEnabled)
                route "/dev/seed" (postDevSeedHandler db devModeEnabled) ]

          // Page 1: Login & Smart Routing (`src/pages/login/login.server.fs`)
          yield! Rashmatz.Pages.Login.Server.endpoints db googleValidator devModeEnabled

          // Page 2: My Custody & Physical Possession (`src/pages/my-custody/my-custody.server.fs`)
          yield! Rashmatz.Pages.MyCustody.Server.endpoints db devModeEnabled

          // Page 5: Quick Physical Relocation (`src/pages/relocate/relocate.server.fs`)
          yield! Rashmatz.Pages.Relocate.Server.endpoints db devModeEnabled

          // Page 6A: Transfer Step 1 - Template Selection (`src/pages/transfers-new-template/transfers-new-template.server.fs`)
          yield! Rashmatz.Pages.TransfersNewTemplate.Server.endpoints db devModeEnabled

          // Page 6B: Transfer Step 2 - Parties Selection (`src/pages/transfers-new-parties/transfers-new-parties.server.fs`)
          yield! Rashmatz.Pages.TransfersNewParties.Server.endpoints db devModeEnabled

          // Page 6C: Transfer Step 3 - Items, Balances, Intake & Execution (`src/pages/transfers-new-items/TransfersNewItems.Endpoints.fs`)
          yield! Rashmatz.Pages.TransfersNewItems.Endpoints.endpoints db devModeEnabled ]

module Program =

    let configureApp
        (app: IApplicationBuilder)
        (db: IDbContext)
        (googleValidator: IGoogleTokenValidator)
        (devModeEnabled: bool)
        =
        AppRoutes.buildEndpoints db googleValidator devModeEnabled
        |> app.UseRouting().UseOxpecker
        |> ignore

    [<EntryPoint>]
    let main args =
        let initOnly = args |> Array.exists (fun a -> a = "--init-db")
        let seedAndExit = args |> Array.exists (fun a -> a = "--seed-db")

        let builder = WebApplication.CreateBuilder args

        let connString =
            ofNullableTrimmed builder.Configuration["ConnectionStrings:Sqlite"]
            |> Option.defaultValue "Data Source=rashmatz.db"

        // Constructing SqliteDbContext automatically creates rashmatz.db if missing,
        // enables WAL + foreign_keys + busy_timeout, and creates all 17 tables & indexes!
        let dbContext = Database.SqliteDbContext connString :> IDbContext

        if initOnly then
            printfn "[Rashmatz DB] SQLite schema and WAL mode initialized successfully (%s)." connString
            0
        elif seedAndExit then
            let summary = Database.ensureDevelopmentSeed dbContext
            printfn "[Rashmatz DB] SQLite database initialized and seeded (%s):" connString
            printfn "  Organization ID : %s (%s)" summary.OrganizationId summary.OrganizationSlug
            printfn "  Admin Header    : X-Dev-User-Email: %s (Holder: %s)" summary.AdminEmail summary.AdminHolderId
            printfn "  Member Header   : X-Dev-User-Email: %s (Holder: %s)" summary.MemberEmail summary.MemberHolderId
            printfn "  Warehouse ID    : %s" summary.WarehouseHolderId
            printfn "  Template ID     : %s" summary.TemplateId
            0
        else
            let devFlag =
                ofNullableTrimmed builder.Configuration["Auth:EnableDevMode"]
                |> Option.map (fun s -> String.Equals(s, "true", StringComparison.OrdinalIgnoreCase))
                |> Option.defaultValue false

            let devModeEnabled = builder.Environment.IsDevelopment() || devFlag

            if devModeEnabled then
                let summary = Database.ensureDevelopmentSeed dbContext
                printfn "[Rashmatz Dev Mode] SQLite DB ready (%s) | Org: %s | Admin: %s | Member: %s"
                    connString
                    summary.OrganizationSlug
                    summary.AdminEmail
                    summary.MemberEmail

            dbContext |> builder.Services.AddSingleton<IDbContext> |> ignore
            builder.Services.AddHttpClient() |> ignore

            builder.Services.AddSingleton<IGoogleTokenValidator>(fun sp ->
                let factory = sp.GetRequiredService<IHttpClientFactory>()
                ProductionGoogleTokenValidator(factory.CreateClient()) :> IGoogleTokenValidator)
            |> ignore

            builder.Services.AddRouting() |> ignore
            builder.Services.AddOxpecker() |> ignore

            let app = builder.Build()
            let validator = app.Services.GetRequiredService<IGoogleTokenValidator>()
            configureApp app dbContext validator devModeEnabled

            app.Run()
            0
