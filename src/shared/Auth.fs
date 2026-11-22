namespace Rashmatz.Shared

open System
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Donald
open FsToolkit.ErrorHandling
open Rashmatz.Shared.DomainTypes

/// Verified Google user payload returned by the injected IGoogleTokenValidator dependency.
type GoogleUserPayload =
    { GoogleSubject: string
      Email: string
      FullName: string
      AvatarUrl: string option }

/// Injected dependency for verifying Google OAuth tokens.
/// Allows unit and integration tests to inject a deterministic mock validator without calling Google servers.
type IGoogleTokenValidator =
    abstract member ValidateIdTokenAsync: idToken: string -> Task<Result<GoogleUserPayload, ApiError>>

/// Organization membership entry for a user across tenants.
type UserOrgMembership =
    { OrganizationId: OrgId
      OrganizationName: string
      OrganizationSlug: string
      HolderId: HolderId
      HolderName: string
      DepartmentId: string option
      InternalIdentifier: string option
      Role: SystemRole
      AttendanceStatus: AttendanceStatus }

/// Authenticated principal resolved for the current HTTP request and active organization.
type AuthenticatedContext =
    { UserId: UserId
      Email: string
      FullName: string
      AvatarUrl: string option
      IsSuperAdmin: bool
      ActiveOrg: UserOrgMembership
      AvailableOrgs: UserOrgMembership list }

module Auth =

    [<Literal>]
    let SessionCookieName = "rashmatz_session"

    [<Literal>]
    let ActiveOrgCookieName = "active_org_id"

    [<Literal>]
    let ActiveOrgHeaderName = "X-Active-Org-Id"

    [<Literal>]
    let DevUserEmailHeaderName = "X-Dev-User-Email"

    let hashSessionToken (rawToken: string) : string =
        let bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))
        Convert.ToHexString(bytes).ToLowerInvariant()

    let generateSecureSessionToken () : string =
        let bytes = RandomNumberGenerator.GetBytes(32)
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')

    /// Queries all active organizations where the user has an active custody_holders record
    /// (matching either user_id or invited_email).
    let loadUserMemberships
        (conn: Microsoft.Data.Sqlite.SqliteConnection)
        (userId: UserId)
        (email: string)
        : UserOrgMembership list =
        conn
        |> Db.newCommand
            """
            SELECT
                o.id AS org_id,
                o.name AS org_name,
                o.slug AS org_slug,
                ch.id AS holder_id,
                ch.name AS holder_name,
                ch.department_id,
                ch.internal_identifier,
                ch.role,
                ch.attendance_status
            FROM custody_holders ch
            INNER JOIN organizations o ON o.id = ch.organization_id
            WHERE o.is_active = 1
              AND ch.is_active = 1
              AND ch.holder_type = 1
              AND ch.role > 0
              AND (ch.user_id = @user_id OR LOWER(ch.invited_email) = LOWER(@email))
            ORDER BY o.name ASC
            """
        |> Db.setParams
            [ "user_id", SqlType.String(userId.ToDbString())
              "email", SqlType.String(email.Trim().ToLowerInvariant()) ]
        |> Db.query (fun rd ->
            { OrganizationId = OrgId(Guid.Parse(rd.ReadString "org_id"))
              OrganizationName = rd.ReadString "org_name"
              OrganizationSlug = rd.ReadString "org_slug"
              HolderId = HolderId(Guid.Parse(rd.ReadString "holder_id"))
              HolderName = rd.ReadString "holder_name"
              DepartmentId = rd.ReadStringOption "department_id"
              InternalIdentifier = rd.ReadStringOption "internal_identifier"
              Role = LanguagePrimitives.EnumOfValue<int, SystemRole>(rd.ReadInt32 "role")
              AttendanceStatus =
                LanguagePrimitives.EnumOfValue<int, AttendanceStatus>(rd.ReadInt32 "attendance_status") })

    let private resolveRequestedOrgId (ctx: HttpContext) (sessionActiveOrgId: string option) : Guid option =
        let fromHeader =
            match ctx.Request.Headers.TryGetValue(ActiveOrgHeaderName) with
            | true, v when v.Count > 0 -> ofNullableTrimmed v[0]
            | _ -> None

        let fromCookie =
            match ctx.Request.Cookies.TryGetValue(ActiveOrgCookieName) with
            | true, v -> ofNullableTrimmed v
            | _ -> None

        [ fromHeader; fromCookie; sessionActiveOrgId ]
        |> List.tryPick (fun opt ->
            match opt with
            | Some s ->
                match Guid.TryParse(s) with
                | true, g -> Some g
                | _ -> None
            | None -> None)

    let private buildAuthenticatedContext
        (conn: Microsoft.Data.Sqlite.SqliteConnection)
        (ctx: HttpContext)
        (userId: UserId)
        (email: string)
        (fullName: string)
        (avatarUrl: string option)
        (isSuperAdmin: bool)
        (sessionActiveOrgId: string option)
        : Result<AuthenticatedContext, ApiError> =
        let memberships = loadUserMemberships conn userId email

        match memberships with
        | [] -> Error(ApiError.single ApiErrorCode.EmailNotWhitelisted)
        | firstMembership :: _ ->
            let requestedOrgGuidOpt = resolveRequestedOrgId ctx sessionActiveOrgId

            let activeMembership =
                match requestedOrgGuidOpt with
                | Some reqGuid ->
                    memberships
                    |> List.tryFind (fun m -> m.OrganizationId.Value = reqGuid)
                    |> Option.defaultValue firstMembership
                | None -> firstMembership

            Ok
                { UserId = userId
                  Email = email
                  FullName = fullName
                  AvatarUrl = avatarUrl
                  IsSuperAdmin = isSuperAdmin
                  ActiveOrg = activeMembership
                  AvailableOrgs = memberships }

    /// Resolves the authenticated user from either:
    /// 1. Developer mode header (`X-Dev-User-Email`) when devModeEnabled = true (for headless API testing without UI).
    /// 2. Standard 30-day `HttpOnly` session cookie (`rashmatz_session`).
    let authenticateRequest
        (db: IDbContext)
        (devModeEnabled: bool)
        (ctx: HttpContext)
        : Result<AuthenticatedContext, ApiError> =
        use conn = db.OpenReadConnection()

        let devHeaderEmailOpt =
            if devModeEnabled then
                match ctx.Request.Headers.TryGetValue(DevUserEmailHeaderName) with
                | true, v when v.Count > 0 ->
                    ofNullableTrimmed v[0] |> Option.map (fun s -> s.ToLowerInvariant())
                | _ -> None
            else
                None

        match devHeaderEmailOpt with
        | Some devEmail ->
            let userRowOpt =
                conn
                |> Db.newCommand
                    """
                    SELECT id, email, full_name, avatar_url, is_super_admin
                    FROM users
                    WHERE LOWER(email) = @email
                    """
                |> Db.setParams [ "email", SqlType.String devEmail ]
                |> Db.querySingle (fun rd ->
                    UserId(Guid.Parse(rd.ReadString "id")),
                    rd.ReadString "email",
                    rd.ReadString "full_name",
                    rd.ReadStringOption "avatar_url",
                    rd.ReadInt32 "is_super_admin" = 1)

            match userRowOpt with
            | Some(uId, email, fullName, avatarOpt, isSuperAdmin) ->
                buildAuthenticatedContext conn ctx uId email fullName avatarOpt isSuperAdmin None
            | None -> Error(ApiError.single ApiErrorCode.EmailNotWhitelisted)

        | None ->
            let cookieTokenOpt =
                match ctx.Request.Cookies.TryGetValue(SessionCookieName) with
                | true, raw -> ofNullableTrimmed raw
                | false, _ -> None

            match cookieTokenOpt with
            | None -> Error(ApiError.single ApiErrorCode.Unauthenticated)
            | Some rawToken ->
                let tokenHash = hashSessionToken rawToken
                let nowIso = DateTimeOffset.UtcNow.ToString("O")

                let sessionUserOpt =
                    conn
                    |> Db.newCommand
                        """
                        SELECT
                            u.id,
                            u.email,
                            u.full_name,
                            u.avatar_url,
                            u.is_super_admin,
                            s.active_org_id
                        FROM user_sessions s
                        INNER JOIN users u ON u.id = s.user_id
                        WHERE s.token_hash = @token_hash
                          AND s.expires_at > @now
                        """
                    |> Db.setParams
                        [ "token_hash", SqlType.String tokenHash
                          "now", SqlType.String nowIso ]
                    |> Db.querySingle (fun rd ->
                        UserId(Guid.Parse(rd.ReadString "id")),
                        rd.ReadString "email",
                        rd.ReadString "full_name",
                        rd.ReadStringOption "avatar_url",
                        rd.ReadInt32 "is_super_admin" = 1,
                        rd.ReadStringOption "active_org_id")

                match sessionUserOpt with
                | None -> Error(ApiError.single ApiErrorCode.Unauthenticated)
                | Some(uId, email, fullName, avatarOpt, isSuperAdmin, sessionOrgOpt) ->
                    buildAuthenticatedContext conn ctx uId email fullName avatarOpt isSuperAdmin sessionOrgOpt

    /// Railway Oriented Programming guard requiring Member or Admin role.
    let requireMember (db: IDbContext) (devMode: bool) (ctx: HttpContext) : Result<AuthenticatedContext, ApiError> =
        authenticateRequest db devMode ctx

    /// Railway Oriented Programming guard requiring Admin role (role = 2).
    let requireAdmin (db: IDbContext) (devMode: bool) (ctx: HttpContext) : Result<AuthenticatedContext, ApiError> =
        result {
            let! auth = authenticateRequest db devMode ctx

            if auth.ActiveOrg.Role <> SystemRole.Admin then
                return! Error(ApiError.single ApiErrorCode.ForbiddenRole)
            else
                return auth
        }

    /// Railway Oriented Programming guard requiring Super-Admin privilege.
    let requireSuperAdmin (db: IDbContext) (devMode: bool) (ctx: HttpContext) : Result<AuthenticatedContext, ApiError> =
        result {
            let! auth = authenticateRequest db devMode ctx

            if not auth.IsSuperAdmin then
                return! Error(ApiError.single ApiErrorCode.ForbiddenSuperAdminOnly)
            else
                return auth
        }

    let issueSessionCookie (ctx: HttpContext) (rawToken: string) (activeOrgId: OrgId) =
        let cookieOptions =
            CookieOptions(
                HttpOnly = true,
                Secure = ctx.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                Expires = DateTimeOffset.UtcNow.AddDays(30.0)
            )

        ctx.Response.Cookies.Append(SessionCookieName, rawToken, cookieOptions)

        let orgCookieOptions =
            CookieOptions(
                HttpOnly = false,
                Secure = ctx.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                Expires = DateTimeOffset.UtcNow.AddDays(30.0)
            )

        ctx.Response.Cookies.Append(ActiveOrgCookieName, activeOrgId.ToDbString(), orgCookieOptions)
