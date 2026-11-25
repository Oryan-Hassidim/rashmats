module Auth

open System
open System.Security.Claims
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Authentication.Google
open Microsoft.AspNetCore.Authentication.OAuth
open Microsoft.AspNetCore.Authorization
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Oxpecker

// --- Domain Types (Scott Wlaschin style: make illegal states unrepresentable) ---

type AuthError =
    | MissingPrincipal
    | MissingIdentity
    | MissingClaim of claimType: string
    | InvalidClaim of claimType: string * reason: string
    | EmailNotWhitelisted of email: string
    | OrganizationInactive

module AuthError =
    let code = function
        | MissingPrincipal -> "MISSING_PRINCIPAL"
        | MissingIdentity -> "MISSING_IDENTITY"
        | MissingClaim _ -> "MISSING_CLAIM"
        | InvalidClaim _ -> "INVALID_CLAIM"
        | EmailNotWhitelisted _ -> "EMAIL_NOT_WHITELISTED"
        | OrganizationInactive -> "ORGANIZATION_INACTIVE"

    let describe = function
        | MissingPrincipal -> "Authentication ticket did not contain a ClaimsPrincipal."
        | MissingIdentity -> "Authentication ticket did not contain a ClaimsIdentity."
        | MissingClaim claimType -> $"Missing required claim '{claimType}'."
        | InvalidClaim (claimType, reason) -> $"Invalid claim '{claimType}': {reason}"
        | EmailNotWhitelisted email -> $"Email '{email}' is not whitelisted in any active organization."
        | OrganizationInactive -> "The organization is inactive."

// TODO: replace all IDs to use [<Mesaure>]
type UserId = private UserId of Guid

module UserId =
    let create () = UserId(Guid.CreateVersion7())
    let fromGuid (id: Guid) = UserId id
    let tryParse (raw: string) =
        match Guid.TryParse(raw) with
        | true, guid -> Some(UserId guid)
        | false, _ -> None
    let value (UserId id) = id
    let toString (UserId id) = id.ToString("D")

type OrgId = private OrgId of Guid

module OrgId =
    let create () = OrgId(Guid.CreateVersion7())
    let fromGuid (id: Guid) = OrgId id
    let tryParse (raw: string) =
        match Guid.TryParse(raw) with
        | true, guid -> Some(OrgId guid)
        | false, _ -> None
    let value (OrgId id) = id
    let toString (OrgId id) = id.ToString("D")

type HolderId = private HolderId of Guid

module HolderId =
    let create () = HolderId(Guid.CreateVersion7())
    let fromGuid (id: Guid) = HolderId id
    let value (HolderId id) = id
    let toString (HolderId id) = id.ToString("D")

type GoogleSub = private GoogleSub of string

module GoogleSub =
    let create (raw: string) =
        if String.IsNullOrWhiteSpace raw then
            Error(InvalidClaim(ClaimTypes.NameIdentifier, "Subject cannot be empty."))
        else
            Ok(GoogleSub(raw.Trim()))

    let value (GoogleSub sub) = sub

type EmailAddress = private EmailAddress of string

module EmailAddress =
    let create (raw: string) =
        let trimmed = raw.Trim()

        // TODO: Check with built in function for that
        if String.IsNullOrWhiteSpace trimmed || not (trimmed.Contains('@')) then
            Error(InvalidClaim(ClaimTypes.Email, "Value is not a valid email address."))
        else
            Ok(EmailAddress(trimmed.ToLowerInvariant()))

    let value (EmailAddress email) = email

type FullName = private FullName of string

module FullName =
    let create (raw: string) =
        if String.IsNullOrWhiteSpace raw then
            Error(InvalidClaim(ClaimTypes.Name, "Name cannot be empty."))
        else
            Ok(FullName(raw.Trim()))

    let value (FullName name) = name

/// Roles that grant system access within an organization (role = 1 Member, 2 Admin).
/// Role 0 (no system access) is intentionally unrepresentable here.
type TenantRole =
    | Member
    | Admin

module TenantRole =
    let tryParse (raw: string) : TenantRole option =
        if String.IsNullOrWhiteSpace raw then None
        else
            match raw.Trim().ToLowerInvariant() with
            | "member" | "1" -> Some Member
            | "admin" | "2" -> Some Admin
            | _ -> None

    let toString = function
        | Member -> "Member"
        | Admin -> "Admin"

    let toCode = function
        | Member -> 1
        | Admin -> 2

    /// Admins also inherit all Member permissions ("An Admin is First and Foremost a User!").
    let satisfies (required: TenantRole) (actual: TenantRole) : bool =
        match required, actual with
        | Member, Member
        | Member, Admin
        | Admin, Admin -> true
        | Admin, Member -> false

type OrgMembership = {
    OrgId: OrgId
    OrgSlug: string
    OrgName: string
    OrgIsActive: bool
    HolderId: HolderId
    HolderIsActive: bool
    Role: TenantRole
}

type UserAuthContext = {
    UserId: UserId
    Email: EmailAddress
    FullName: FullName
    IsSuperAdmin: bool
    Memberships: OrgMembership list
}

type AuthorizedTenantContext = {
    UserId: UserId
    Email: EmailAddress
    FullName: FullName
    IsSuperAdmin: bool
    ActiveOrgId: OrgId
    ActiveOrgSlug: string
    ActiveOrgName: string
    HolderId: HolderId
    Role: TenantRole
    AvailableOrgs: OrgMembership list
}

type GoogleUserInfo = {
    GoogleSub: GoogleSub
    Email: EmailAddress
    FullName: FullName
}

/// Functional dependency (port) for persisting or updating an authenticated Google user.
type UpsertGoogleUser = GoogleUserInfo -> Task<Result<UserId, AuthError>>

/// Functional dependency (port) for loading a user's active tenant memberships and roles.
type ResolveUserAuthContext = UserId -> Task<Result<UserAuthContext, AuthError>>

// --- Railway Oriented Programming Helpers ---

module private Result =
    let requireSome error = function
        | Some value -> Ok value
        | None -> Error error

type private ResultBuilder() =
    member _.Return (x) = Ok x
    member _.Bind (x, f) = Result.bind f x

let private result = ResultBuilder()

// --- Constants & Policies ---

[<Literal>]
let AppUserIdClaim = "app_user_id"

[<Literal>]
let ActiveOrgIdCookieName = "active_org_id"

[<Literal>]
let ActiveOrgSlugCookieName = "active_org_slug"

[<Literal>]
let ActiveOrgIdHeaderName = "X-Org-Id"

[<Literal>]
let ActiveOrgSlugHeaderName = "X-Org-Slug"

[<Literal>]
let private AuthErrorPropertyKey = "rashmatz_auth_error_code"

[<Literal>]
let private UserAuthCacheItemKey = "rashmatz_user_auth_context"

[<Literal>]
let private TenantContextItemKey = "rashmatz_authorized_tenant_context"

module Policies =
    [<Literal>]
    let Member = "Rashmatz.Member"

    [<Literal>]
    let Admin = "Rashmatz.Admin"

// --- Ticket Validation & Claims Mapping ---

let private tryGetClaim (principal: ClaimsPrincipal) (claimType: string) : Result<string, AuthError> =
    principal.FindFirstValue(claimType)
    |> Option.ofObj
    |> Option.filter (String.IsNullOrWhiteSpace >> not)
    |> Result.requireSome (MissingClaim claimType)

let private extractIdentity (principal: ClaimsPrincipal) : Result<ClaimsIdentity, AuthError> =
    match principal.Identity with
    | :? ClaimsIdentity as identity -> Ok identity
    | _ -> Error MissingIdentity

let private extractGoogleUser (principal: ClaimsPrincipal) : Result<GoogleUserInfo, AuthError> =
    result {
        let! rawSub = tryGetClaim principal ClaimTypes.NameIdentifier
        let! googleSub = GoogleSub.create rawSub
        let! rawEmail = tryGetClaim principal ClaimTypes.Email
        let! email = EmailAddress.create rawEmail

        let rawName =
            principal.FindFirstValue(ClaimTypes.Name)
            |> Option.ofObj
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.defaultWith (fun () -> EmailAddress.value email)

        let! fullName = FullName.create rawName

        return {
            GoogleSub = googleSub
            Email = email
            FullName = fullName
        }
    }

let private extractTicketInfo (principalOpt: ClaimsPrincipal option) : Result<ClaimsIdentity * GoogleUserInfo, AuthError> =
    result {
        let! principal = principalOpt |> Result.requireSome MissingPrincipal
        let! identity = extractIdentity principal
        let! userInfo = extractGoogleUser principal
        return identity, userInfo
    }

let private handleCreatingTicket (upsertGoogleUser: UpsertGoogleUser) (ctx: OAuthCreatingTicketContext) : Task =
    task {
        match extractTicketInfo (Option.ofObj ctx.Principal) with
        | Error err ->
            ctx.Properties.Items[AuthErrorPropertyKey] <- AuthError.code err
            ctx.Fail(AuthError.describe err)
        | Ok (identity, userInfo) ->
            // יצירה או עדכון של המשתמש בטבלת users וקבלת ה-UserId הפנימי שלך
            match! upsertGoogleUser userInfo with
            | Error err ->
                ctx.Properties.Items[AuthErrorPropertyKey] <- AuthError.code err
                ctx.Fail(AuthError.describe err)
            | Ok internalUserId ->
                // הוספת ה-UserId הפנימי כ-Claim לתוך ה-Cookie
                identity.AddClaim(Claim(AppUserIdClaim, UserId.toString internalUserId))
    }

let private requireConfig (config: IConfiguration) (key: string) : string =
    config[key]
    |> Option.ofObj
    |> Option.filter (String.IsNullOrWhiteSpace >> not)
    |> Option.defaultWith (fun () -> invalidOp $"Missing required configuration value: '{key}'.")

let private optionalConfig (config: IConfiguration) (key: string) : string option =
    config[key]
    |> Option.ofObj
    |> Option.map (fun s -> s.Trim())
    |> Option.filter (String.IsNullOrWhiteSpace >> not)

// --- Active Tenant & Role Authorization ---

type TenantRoleRequirement(minimumRole: TenantRole) =
    interface IAuthorizationRequirement
    member _.MinimumRole = minimumRole

let private tryGetHeaderOrCookie (ctx: HttpContext) (headerName: string) (cookieName: string) : string option =
    match ctx.Request.Headers.TryGetValue headerName with
    | true, values when values.Count > 0 ->
        values[0]
        |> Option.ofObj
        |> Option.map _.Trim()
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
    | _ ->
        match ctx.Request.Cookies.TryGetValue(cookieName) with
        | true, value ->
            value
            |> Option.ofObj
            |> Option.map _.Trim()
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
        | _ -> None

let private resolveActiveMembership
    (ctx: HttpContext)
    (memberships: OrgMembership list)
    : OrgMembership option =
    let requestedOrgId =
        tryGetHeaderOrCookie ctx ActiveOrgIdHeaderName ActiveOrgIdCookieName
        |> Option.bind OrgId.tryParse

    let requestedOrgSlug =
        tryGetHeaderOrCookie ctx ActiveOrgSlugHeaderName ActiveOrgSlugCookieName

    let activeMemberships =
        memberships |> List.filter (fun m -> m.OrgIsActive && m.HolderIsActive)

    match requestedOrgId, requestedOrgSlug with
    | Some orgId, _ ->
        activeMemberships
        |> List.tryFind (fun m -> OrgId.value m.OrgId = OrgId.value orgId)
    | None, Some slug ->
        activeMemberships
        |> List.tryFind (fun m -> String.Equals(m.OrgSlug, slug, StringComparison.OrdinalIgnoreCase))
    | None, None ->
        activeMemberships |> List.tryHead

let private getOrLoadUserAuthContext
    (httpCtx: HttpContext)
    (resolveUserAuthContext: ResolveUserAuthContext)
    (userId: UserId)
    : Task<Result<UserAuthContext, AuthError>> =
    match httpCtx.Items.TryGetValue(UserAuthCacheItemKey) with
    | true, (:? UserAuthContext as cached) -> Task.FromResult(Ok cached)
    | _ ->
        task {
            let! res = resolveUserAuthContext userId
            match res with
            | Ok userAuth -> httpCtx.Items[UserAuthCacheItemKey] <- userAuth
            | Error _ -> ()
            return res
        }

type TenantRoleHandler
    (resolveUserAuthContext: ResolveUserAuthContext, httpContextAccessor: IHttpContextAccessor) =
    inherit AuthorizationHandler<TenantRoleRequirement>()

    override _.HandleRequirementAsync(context: AuthorizationHandlerContext, requirement: TenantRoleRequirement) =
        task {
            let httpCtxOpt =
                match context.Resource with
                | :? HttpContext as h -> Some h
                | _ -> Option.ofObj httpContextAccessor.HttpContext

            let userIdOpt =
                match Option.ofObj context.User.Identity with
                | Some identity when identity.IsAuthenticated ->
                    context.User.FindFirstValue(AppUserIdClaim)
                    |> Option.ofObj
                    |> Option.bind UserId.tryParse
                | _ -> None

            match httpCtxOpt, userIdOpt with
            | Some httpCtx, Some userId ->
                match! getOrLoadUserAuthContext httpCtx resolveUserAuthContext userId with
                | Error _ -> ()
                | Ok userAuth ->
                    let activeMemberships =
                        userAuth.Memberships
                        |> List.filter (fun m -> m.OrgIsActive && m.HolderIsActive)

                    match resolveActiveMembership httpCtx userAuth.Memberships with
                    | Some membership when TenantRole.satisfies requirement.MinimumRole membership.Role ->
                        let tenantCtx : AuthorizedTenantContext = {
                            UserId = userAuth.UserId
                            Email = userAuth.Email
                            FullName = userAuth.FullName
                            IsSuperAdmin = userAuth.IsSuperAdmin
                            ActiveOrgId = membership.OrgId
                            ActiveOrgSlug = membership.OrgSlug
                            ActiveOrgName = membership.OrgName
                            HolderId = membership.HolderId
                            Role = membership.Role
                            AvailableOrgs = activeMemberships
                        }
                        httpCtx.Items[TenantContextItemKey] <- tenantCtx
                        context.Succeed(requirement)
                    | _ -> ()
            | _ -> ()
        }

let tryGetTenantContext (ctx: HttpContext) : AuthorizedTenantContext option =
    match ctx.Items.TryGetValue(TenantContextItemKey) with
    | true, (:? AuthorizedTenantContext as tenantCtx) -> Some tenantCtx
    | _ -> None

// --- Oxpecker Endpoint Authorization Helpers ---

let requireMember (endpoint: Endpoint) : Endpoint =
    endpoint |> configureEndpoint _.RequireAuthorization(Policies.Member)

let requireAdmin (endpoint: Endpoint) : Endpoint =
    endpoint |> configureEndpoint _.RequireAuthorization(Policies.Admin)

// --- ASP.NET Core Auth Configuration & Oxpecker Endpoints ---

let private isSafeInternalPath (path: string) : bool =
    not (String.IsNullOrWhiteSpace path)
    && path.StartsWith('/')
    && not (path.StartsWith("//"))
    && not (path.StartsWith("/\\"))

let private frontendBaseUrl (config: IConfiguration) : string =
    optionalConfig config "Authentication:FrontendBaseUrl"
    |> Option.defaultValue ""

let private buildRedirectUrl (config: IConfiguration) (relativePath: string) : string =
    match frontendBaseUrl config with
    | "" -> relativePath
    | baseUrl -> baseUrl.TrimEnd('/') + relativePath

let configureAuth
    (config: IConfiguration)
    (env: IHostEnvironment)
    (upsertGoogleUser: UpsertGoogleUser)
    (resolveUserAuthContext: ResolveUserAuthContext)
    (services: IServiceCollection)
    : IServiceCollection =
    let isDev = env.IsDevelopment()

    services.AddHttpContextAccessor() |> ignore
    services.AddSingleton<ResolveUserAuthContext>(resolveUserAuthContext) |> ignore
    services.AddScoped<IAuthorizationHandler, TenantRoleHandler>() |> ignore

    services
        .AddAuthorization(fun options ->
            options.AddPolicy(Policies.Member, fun policy ->
                policy.RequireAuthenticatedUser() |> ignore
                policy.AddRequirements(TenantRoleRequirement(Member)) |> ignore)
            options.AddPolicy(Policies.Admin, fun policy ->
                policy.RequireAuthenticatedUser() |> ignore
                policy.AddRequirements(TenantRoleRequirement(Admin)) |> ignore))
    |> ignore

    services
        .AddAuthentication(fun options ->
            // הסשן השוטף מנוהל ע"י Cookie; ברירת המחדל ל-Challenge היא Cookie כדי שקריאות לא מאומתות יקבלו 401
            // (התחברות לגוגל מבקשת Challenge מפורש ל-GoogleDefaults.AuthenticationScheme ב-/auth/login/google)
            options.DefaultScheme <- CookieAuthenticationDefaults.AuthenticationScheme
            options.DefaultChallengeScheme <- CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(fun options ->
            options.Cookie.Name <- "auth_session"
            options.Cookie.HttpOnly <- true
            // In Development over http://localhost:5000, SameAsRequest + Lax lets browsers and tools send cookies over HTTP.
            options.Cookie.SecurePolicy <-
                if isDev then CookieSecurePolicy.SameAsRequest else CookieSecurePolicy.Always
            options.Cookie.SameSite <-
                if isDev then SameSiteMode.Lax else SameSiteMode.None

            // בקריאות API לא עושים Redirect לגוגל, אלא מחזירים 401
            options.Events.OnRedirectToLogin <- fun ctx ->
                ctx.Response.StatusCode <- StatusCodes.Status401Unauthorized
                Task.CompletedTask
            options.Events.OnRedirectToAccessDenied <- fun ctx ->
                ctx.Response.StatusCode <- StatusCodes.Status403Forbidden
                Task.CompletedTask)
        .AddGoogle(fun options ->
            options.ClientId <- requireConfig config "Authentication:Google:ClientId"
            options.ClientSecret <- requireConfig config "Authentication:Google:ClientSecret"
            // הנתיב אליו גוגל מחזיר את המשתמש (חייב להיות מוגדר ב-Google Cloud Console)
            options.CallbackPath <- PathString("/signin-google")

            // הרגע שבו המשתמש חזר מגוגל בהצלחה - הזדמנות לסנכרן מול ה-DB שלך
            options.Events.OnCreatingTicket <- fun ctx ->
                handleCreatingTicket upsertGoogleUser ctx

            options.Events.OnRemoteFailure <- fun ctx ->
                let errorCode =
                    match Option.ofObj ctx.Properties with
                    | None -> "AUTH_FAILED"
                    | Some props ->
                        match props.Items.TryGetValue(AuthErrorPropertyKey) with
                        | true, code ->
                            code
                            |> Option.ofObj
                            |> Option.filter (String.IsNullOrWhiteSpace >> not)
                            |> Option.defaultValue "AUTH_FAILED"
                        | _ -> "AUTH_FAILED"

                ctx.HandleResponse()
                let loginErrorUrl = buildRedirectUrl config $"/login?error={Uri.EscapeDataString(errorCode)}"
                ctx.Response.Redirect(loginErrorUrl)
                Task.CompletedTask)
    |> ignore

    services

[<CLIMutable>]
type SwitchOrgRequest = {
    org_id: string option
    org_slug: string option
}

let private meHandler : EndpointHandler =
    fun ctx ->
        match tryGetTenantContext ctx with
        | None ->
            ctx.SetStatusCode StatusCodes.Status401Unauthorized
            Task.CompletedTask
        | Some tenantCtx ->
            ctx.WriteJson {|
                user = {|
                    id = UserId.toString tenantCtx.UserId
                    email = EmailAddress.value tenantCtx.Email
                    full_name = FullName.value tenantCtx.FullName
                    is_super_admin = tenantCtx.IsSuperAdmin
                |}
                active_organization = {|
                    id = OrgId.toString tenantCtx.ActiveOrgId
                    slug = tenantCtx.ActiveOrgSlug
                    name = tenantCtx.ActiveOrgName
                    holder_id = HolderId.toString tenantCtx.HolderId
                    role = TenantRole.toString tenantCtx.Role
                    role_code = TenantRole.toCode tenantCtx.Role
                |}
                available_organizations =
                    tenantCtx.AvailableOrgs
                    |> List.map (fun m -> {|
                        id = OrgId.toString m.OrgId
                        slug = m.OrgSlug
                        name = m.OrgName
                        holder_id = HolderId.toString m.HolderId
                        role = TenantRole.toString m.Role
                        role_code = TenantRole.toCode m.Role
                    |})
            |}

let private switchOrgHandler : EndpointHandler =
    fun ctx ->
        task {
            match tryGetTenantContext ctx with
            | None ->
                ctx.SetStatusCode StatusCodes.Status401Unauthorized
            | Some tenantCtx ->
                let! body = ctx.BindJson<SwitchOrgRequest>()

                let targetOrg =
                    match body.org_id |> Option.bind OrgId.tryParse, body.org_slug with
                    | Some id, _ ->
                        tenantCtx.AvailableOrgs
                        |> List.tryFind (fun m -> OrgId.value m.OrgId = OrgId.value id)
                    | None, Some slug when not (String.IsNullOrWhiteSpace slug) ->
                        tenantCtx.AvailableOrgs
                        |> List.tryFind (fun m -> String.Equals(m.OrgSlug, slug.Trim(), StringComparison.OrdinalIgnoreCase))
                    | _ -> None

                match targetOrg with
                | None ->
                    ctx.SetStatusCode StatusCodes.Status403Forbidden
                    do! ctx.WriteJson {| code = "ORGANIZATION_ACCESS_DENIED" |}
                | Some target ->
                    let env = ctx.GetWebHostEnvironment()
                    let isDev = env.IsDevelopment()
                    let cookieOpts =
                        CookieOptions(
                            HttpOnly = false,
                            Secure = not isDev,
                            SameSite = (if isDev then SameSiteMode.Lax else SameSiteMode.None),
                            Path = "/"
                        )
                    ctx.Response.Cookies.Append(ActiveOrgIdCookieName, OrgId.toString target.OrgId, cookieOpts)
                    ctx.Response.Cookies.Append(ActiveOrgSlugCookieName, target.OrgSlug, cookieOpts)

                    do! ctx.WriteJson {|
                        active_org_id = OrgId.toString target.OrgId
                        active_org_slug = target.OrgSlug
                        role = TenantRole.toString target.Role
                        role_code = TenantRole.toCode target.Role
                    |}
        }

let authEndpoints (config: IConfiguration) : Endpoint list = [
    GET [
        // 1. הפרונטאנד מפנה לכאן: GET /auth/login/google?return_to=/my-custody
        route "/auth/login/google" (fun (ctx: HttpContext) ->
            let returnTo =
                ctx.TryGetQueryValue "return_to"
                |> Option.filter isSafeInternalPath
                |> Option.defaultValue "/my-custody"

            let redirectUri = buildRedirectUrl config returnTo
            let props = AuthenticationProperties(RedirectUri = redirectUri)
            ctx.ChallengeAsync(GoogleDefaults.AuthenticationScheme, props))

        // 2. התנתקות: מוחק את ה-Cookie ומחזיר לפרונטאנד
        route "/auth/logout" (fun (ctx: HttpContext) ->
            task {
                do! ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)
                ctx.Response.Cookies.Delete(ActiveOrgIdCookieName)
                ctx.Response.Cookies.Delete(ActiveOrgSlugCookieName)
                ctx.Response.Redirect(buildRedirectUrl config "/login")
            })

        // 3. פרטי המשתמש המחובר, הארגון הפעיל והתפקיד (Member / Admin)
        route "/auth/me" meHandler |> requireMember
    ]
    POST [
        // 4. החלפת ארגון פעיל (Tenant Context Switcher)
        route "/auth/active-org" switchOrgHandler |> requireMember
    ]
]
