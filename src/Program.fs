open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Oxpecker

let private routeSummary (label: string) : EndpointHandler =
    fun ctx ->
        match Auth.tryGetTenantContext ctx with
        | None ->
            ctx.SetStatusCode StatusCodes.Status401Unauthorized
            System.Threading.Tasks.Task.CompletedTask
        | Some tenant ->
            ctx.WriteJson {|
                screen = label
                organization = tenant.ActiveOrgSlug
                role = Auth.TenantRole.toString tenant.Role
                user = Auth.EmailAddress.value tenant.Email
            |}

let endpoints (config: IConfiguration) = [
    yield! Auth.authEndpoints config

    GET [
        // Public routes
        route "/" <| text "Rashmatz API"
        route "/login" (fun (ctx: HttpContext) ->
            let errorCode = ctx.TryGetQueryValue "error" |> Option.defaultValue ""
            ctx.WriteJson {| page = "login"; error = errorCode |})

        // Member & Admin routes (per Client Application Specification §1.2)
        route "/my-custody" (routeSummary "my-custody") |> Auth.requireMember
        route "/my-transfers" (routeSummary "my-transfers") |> Auth.requireMember
        route "/audit/scan" (routeSummary "audit-scan") |> Auth.requireMember

        // Admin-only routes (per Client Application Specification §1.2)
        route "/admin/dashboard" (routeSummary "admin-dashboard") |> Auth.requireAdmin
        route "/transfers/new" (routeSummary "transfers-new") |> Auth.requireAdmin
        route "/transfers/history" (routeSummary "transfers-history") |> Auth.requireAdmin
        route "/admin/inventory" (routeSummary "admin-inventory") |> Auth.requireAdmin
        route "/admin/holders" (routeSummary "admin-holders") |> Auth.requireAdmin
        route "/admin/audits" (routeSummary "admin-audits") |> Auth.requireAdmin
        route "/items/relocate" (routeSummary "items-relocate") |> Auth.requireAdmin
    ]
]

[<EntryPoint>]
let main args =
    let builder = WebApplication.CreateBuilder(args)
    let authStore = MockDb.MockAuthStore(builder.Configuration)

    builder.Services
        .AddRouting()
        .AddOxpecker()
    |> Auth.configureAuth
        builder.Configuration
        builder.Environment
        authStore.UpsertGoogleUser
        authStore.ResolveUserAuthContext
    |> ignore

    let app = builder.Build()

    app.UseRouting()
        .UseAuthentication()
        .UseAuthorization()
        .Use(Default.exceptionMiddleware)
        .UseOxpecker(endpoints builder.Configuration)
        .Run(Default.notFoundHandler)

    app.Run()
    0 // Exit code
