namespace Rashmatz.Shared

open System
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Data.Sqlite
open Donald
open FsToolkit.ErrorHandling

type GoogleUser = { Subject: string; Email: string; FullName: string; AvatarUrl: string option }

/// Injected dependency for Google OAuth token verification (mockable in tests without calling Google).
type IGoogleTokenValidator =
    abstract ValidateAsync: idToken: string -> Task<Result<GoogleUser, ApiError>>

type OrgMembership =
    { OrgId: OrgId
      OrgName: string
      OrgSlug: string
      HolderId: HolderId
      HolderName: string
      DepartmentId: string option
      InternalIdentifier: string option
      Role: SystemRole
      AttendanceStatus: AttendanceStatus }

type AuthContext =
    { UserId: UserId
      Email: string
      FullName: string
      AvatarUrl: string option
      IsSuperAdmin: bool
      ActiveOrg: OrgMembership
      AvailableOrgs: OrgMembership list }

module Auth =
    [<Literal>]
    let SessionCookie = "rashmatz_session"
    [<Literal>]
    let ActiveOrgCookie = "active_org_id"
    [<Literal>]
    let ActiveOrgHeader = "X-Active-Org-Id"
    [<Literal>]
    let DevEmailHeader = "X-Dev-User-Email"

    let hashToken (raw: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes raw) |> Convert.ToHexString |> _.ToLowerInvariant()

    let newSessionToken () =
        RandomNumberGenerator.GetBytes(32) |> Convert.ToBase64String |> _.Replace('+', '-').Replace('/', '_').TrimEnd('=')

    let loadMemberships (conn: SqliteConnection) (userId: UserId) (email: string) =
        conn
        |> Db.newCommand
            """
            SELECT o.id AS org_id, o.name AS org_name, o.slug AS org_slug,
                   ch.id AS holder_id, ch.name AS holder_name, ch.department_id,
                   ch.internal_identifier, ch.role, ch.attendance_status
            FROM custody_holders ch
            INNER JOIN organizations o ON o.id = ch.organization_id
            WHERE o.is_active = 1 AND ch.is_active = 1 AND ch.holder_type = 1 AND ch.role > 0
              AND (ch.user_id = @uid OR LOWER(ch.invited_email) = LOWER(@email))
            ORDER BY o.name ASC
            """
        |> Db.setParams [ "uid", SqlType.String userId.Value; "email", SqlType.String(email.Trim().ToLowerInvariant()) ]
        |> Db.query (fun rd ->
            { OrgId = OrgId(Guid.Parse(rd.ReadString "org_id"))
              OrgName = rd.ReadString "org_name"
              OrgSlug = rd.ReadString "org_slug"
              HolderId = HolderId(Guid.Parse(rd.ReadString "holder_id"))
              HolderName = rd.ReadString "holder_name"
              DepartmentId = rd.ReadStringOption "department_id"
              InternalIdentifier = rd.ReadStringOption "internal_identifier"
              Role = LanguagePrimitives.EnumOfValue(rd.ReadInt32 "role")
              AttendanceStatus = LanguagePrimitives.EnumOfValue(rd.ReadInt32 "attendance_status") })

    let private tryHeader (name: string) (ctx: HttpContext) =
        match ctx.Request.Headers.TryGetValue name with
        | true, v when v.Count > 0 -> trimNullable v[0]
        | _ -> None

    let private tryCookie (name: string) (ctx: HttpContext) =
        match ctx.Request.Cookies.TryGetValue name with
        | true, v -> trimNullable v
        | _ -> None

    let private buildContext conn ctx userId email fullName avatar isSuperAdmin sessionOrgOpt =
        match loadMemberships conn userId email with
        | [] -> Error(ApiError.single ApiErrorCode.EmailNotWhitelisted)
        | first :: _ as all ->
            let requestedOrg =
                [ tryHeader ActiveOrgHeader ctx; tryCookie ActiveOrgCookie ctx; sessionOrgOpt ]
                |> List.tryPick (Option.bind (fun s -> match Guid.TryParse s with true, g -> Some g | _ -> None))

            let active =
                requestedOrg
                |> Option.bind (fun g -> all |> List.tryFind (fun m -> let (OrgId id) = m.OrgId in id = g))
                |> Option.defaultValue first

            Ok { UserId = userId; Email = email; FullName = fullName; AvatarUrl = avatar; IsSuperAdmin = isSuperAdmin; ActiveOrg = active; AvailableOrgs = all }

    let authenticate (db: IDbContext) (devMode: bool) (ctx: HttpContext) : Result<AuthContext, ApiError> =
        use conn = db.OpenRead()
        let devEmail = if devMode then tryHeader DevEmailHeader ctx |> Option.map _.ToLowerInvariant() else None

        match devEmail with
        | Some email ->
            conn
            |> Db.newCommand "SELECT id, email, full_name, avatar_url, is_super_admin FROM users WHERE LOWER(email) = @email"
            |> Db.setParams [ "email", SqlType.String email ]
            |> Db.querySingle (fun rd ->
                UserId(Guid.Parse(rd.ReadString "id")), rd.ReadString "email", rd.ReadString "full_name",
                rd.ReadStringOption "avatar_url", rd.ReadInt32 "is_super_admin" = 1)
            |> function
                | Some(uid, em, name, av, sa) -> buildContext conn ctx uid em name av sa None
                | None -> Error(ApiError.single ApiErrorCode.EmailNotWhitelisted)
        | None ->
            match tryCookie SessionCookie ctx with
            | None -> Error(ApiError.single ApiErrorCode.Unauthenticated)
            | Some rawToken ->
                conn
                |> Db.newCommand
                    """
                    SELECT u.id, u.email, u.full_name, u.avatar_url, u.is_super_admin, s.active_org_id
                    FROM user_sessions s INNER JOIN users u ON u.id = s.user_id
                    WHERE s.token_hash = @hash AND s.expires_at > @now
                    """
                |> Db.setParams [ "hash", SqlType.String(hashToken rawToken); "now", SqlType.String(DateTimeOffset.UtcNow.ToString("O")) ]
                |> Db.querySingle (fun rd ->
                    UserId(Guid.Parse(rd.ReadString "id")), rd.ReadString "email", rd.ReadString "full_name",
                    rd.ReadStringOption "avatar_url", rd.ReadInt32 "is_super_admin" = 1, rd.ReadStringOption "active_org_id")
                |> function
                    | Some(uid, em, name, av, sa, orgOpt) -> buildContext conn ctx uid em name av sa orgOpt
                    | None -> Error(ApiError.single ApiErrorCode.Unauthenticated)

    let requireMember db devMode ctx = authenticate db devMode ctx

    let requireAdmin db devMode ctx =
        result {
            let! auth = authenticate db devMode ctx
            if auth.ActiveOrg.Role <> SystemRole.Admin then return! Error(ApiError.single ApiErrorCode.ForbiddenRole)
            else return auth
        }

    let requireSuperAdmin db devMode ctx =
        result {
            let! auth = authenticate db devMode ctx
            if not auth.IsSuperAdmin then return! Error(ApiError.single ApiErrorCode.ForbiddenSuperAdminOnly)
            else return auth
        }

    let issueCookies (ctx: HttpContext) (rawToken: string) (activeOrgId: OrgId) =
        let expires = DateTimeOffset.UtcNow.AddDays(30.0)
        ctx.Response.Cookies.Append(SessionCookie, rawToken, CookieOptions(HttpOnly = true, Secure = ctx.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/", Expires = expires))
        ctx.Response.Cookies.Append(ActiveOrgCookie, activeOrgId.Str, CookieOptions(HttpOnly = false, Secure = ctx.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/", Expires = expires))
