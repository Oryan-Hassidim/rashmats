namespace Rashmatz.Pages.Login

open System
open System.Text.Json.Serialization
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Oxpecker
open Donald
open FsToolkit.ErrorHandling
open Rashmatz.Shared
open Rashmatz.Shared.DomainTypes

/// Vertical Slice for Page 1: Login & Smart Routing (`/login`)
/// Co-located inside `src/pages/login/login.server.fs` alongside client `index.html`, `login.page.ts`, `login.vm.ts`.
module Server =

    type LoginBootstrapPageDto =
        { [<JsonPropertyName("is_authenticated")>]
          IsAuthenticated: bool
          [<JsonPropertyName("redirect_to")>]
          RedirectTo: string option
          [<JsonPropertyName("return_to")>]
          ReturnTo: string option }

    [<CLIMutable>]
    type GoogleLoginRequestDto =
        { [<JsonPropertyName("id_token")>]
          IdToken: string option
          [<JsonPropertyName("return_to")>]
          ReturnTo: string option }

    [<CLIMutable>]
    type DevLoginRequestDto =
        { [<JsonPropertyName("email")>]
          Email: string option
          [<JsonPropertyName("full_name")>]
          FullName: string option
          [<JsonPropertyName("is_super_admin")>]
          IsSuperAdmin: bool option
          [<JsonPropertyName("return_to")>]
          ReturnTo: string option }

    type LoginSuccessResponseDto =
        { [<JsonPropertyName("user_id")>]
          UserId: string
          [<JsonPropertyName("holder_id")>]
          HolderId: string
          [<JsonPropertyName("active_organization_id")>]
          ActiveOrganizationId: string
          [<JsonPropertyName("role")>]
          Role: int
          [<JsonPropertyName("redirect_to")>]
          RedirectTo: string }

    /// Ensures `return_to` is strictly a relative internal path starting with `/` (preventing Open Redirects).
    let validateReturnToPath (rawOpt: string option) : Result<string option, ApiError> =
        match rawOpt with
        | None -> Ok None
        | Some raw when String.IsNullOrWhiteSpace(raw) -> Ok None
        | Some raw ->
            let trimmed = raw.Trim()

            if
                trimmed.StartsWith("/", StringComparison.Ordinal)
                && not (trimmed.StartsWith("//", StringComparison.Ordinal))
                && not (trimmed.StartsWith("/\\", StringComparison.Ordinal))
                && not (trimmed.StartsWith("/login", StringComparison.OrdinalIgnoreCase))
            then
                Ok(Some trimmed)
            else
                Error(ApiError.single ApiErrorCode.InvalidReturnToPath)

    /// Computes the smart post-login destination per Page 1 Specification:
    /// 1. If a valid `return_to` parameter was provided, route there.
    /// 2. For a Member: `/audit/scan` if an active audit session exists in the organization, else `/my-custody`.
    /// 3. For an Admin: `/admin/dashboard`.
    let computeSmartRedirectAsync
        (db: IDbContext)
        (activeOrg: UserOrgMembership)
        (validatedReturnTo: string option)
        : Task<string> =
        task {
            match validatedReturnTo with
            | Some returnPath -> return returnPath
            | None ->
                use conn = db.OpenReadConnection()
                let activeAuditOpt = Navbar.findActiveAuditSession conn activeOrg.OrganizationId

                match activeOrg.Role, activeAuditOpt with
                | SystemRole.Member, Some _ -> return "/audit/scan"
                | SystemRole.Member, None -> return "/my-custody"
                | SystemRole.Admin, _ -> return "/admin/dashboard"
                | _ -> return "/my-custody"
        }

    /// Core Railway Oriented Programming workflow for authenticating a verified identity
    /// (shared by both Google OAuth login and headless Developer Mode login).
    let completeIdentityLoginAsync
        (db: IDbContext)
        (ctx: HttpContext)
        (payload: GoogleUserPayload)
        (forceSuperAdminOpt: bool option)
        (returnToRaw: string option)
        : Task<Result<LoginSuccessResponseDto, ApiError>> =
        taskResult {
            let! validReturnTo = validateReturnToPath returnToRaw
            let normalizedEmail = payload.Email.Trim().ToLowerInvariant()

            // Verify whitelist and upsert user + session inside the serialized write transaction
            let! (userId, activeMembership) =
                db.ExecuteWriteTransactionAsync(fun conn tx ->
                    task {
                        let whitelistedOrgStates =
                            conn
                            |> Db.newCommand
                                """
                                SELECT o.is_active AS org_active, ch.is_active AS holder_active, ch.role
                                FROM custody_holders ch
                                INNER JOIN organizations o ON o.id = ch.organization_id
                                WHERE LOWER(ch.invited_email) = @email
                                  AND ch.holder_type = 1
                                """
                            |> Db.setTransaction tx
                            |> Db.setParams [ "email", SqlType.String normalizedEmail ]
                            |> Db.query (fun rd ->
                                rd.ReadInt32 "org_active" = 1,
                                rd.ReadInt32 "holder_active" = 1,
                                rd.ReadInt32 "role")

                        let hasActiveMembership =
                            whitelistedOrgStates
                            |> List.exists (fun (orgActive, holderActive, role) ->
                                orgActive && holderActive && role > 0)

                        let hasOnlyInactiveOrg =
                            not hasActiveMembership
                            && (whitelistedOrgStates |> List.exists (fun (orgActive, _, _) -> not orgActive))

                        if hasOnlyInactiveOrg then
                            return Error(ApiError.single ApiErrorCode.OrganizationInactive)
                        elif not hasActiveMembership then
                            return Error(ApiError.single ApiErrorCode.EmailNotWhitelisted)
                        else
                            let nowIso = DateTimeOffset.UtcNow.ToString("O")

                            let existingUserOpt =
                                conn
                                |> Db.newCommand
                                    "SELECT id, is_super_admin FROM users WHERE LOWER(email) = @email OR google_id = @gid"
                                |> Db.setTransaction tx
                                |> Db.setParams
                                    [ "email", SqlType.String normalizedEmail
                                      "gid", SqlType.String payload.GoogleSubject ]
                                |> Db.querySingle (fun rd ->
                                    rd.ReadString "id", rd.ReadInt32 "is_super_admin" = 1)

                            let userIdStr =
                                match existingUserOpt with
                                | Some(existingId, existingSuper) ->
                                    let superFlag =
                                        match forceSuperAdminOpt with
                                        | Some b -> if b then 1 else 0
                                        | None -> if existingSuper then 1 else 0

                                    conn
                                    |> Db.newCommand
                                        """
                                        UPDATE users
                                        SET google_id = @gid,
                                            email = @email,
                                            full_name = @full_name,
                                            avatar_url = @avatar_url,
                                            is_super_admin = @super,
                                            updated_at = @now
                                        WHERE id = @id
                                        """
                                    |> Db.setTransaction tx
                                    |> Db.setParams
                                        [ "gid", SqlType.String payload.GoogleSubject
                                          "email", SqlType.String normalizedEmail
                                          "full_name", SqlType.String payload.FullName
                                          "avatar_url",
                                          (match payload.AvatarUrl with
                                           | Some a -> SqlType.String a
                                           | None -> SqlType.Null)
                                          "super", SqlType.Int32 superFlag
                                          "now", SqlType.String nowIso
                                          "id", SqlType.String existingId ]
                                    |> Db.exec

                                    existingId
                                | None ->
                                    let newId = Guid.NewGuid().ToString("D")
                                    let superFlag =
                                        match forceSuperAdminOpt with
                                        | Some true -> 1
                                        | _ -> 0

                                    conn
                                    |> Db.newCommand
                                        """
                                        INSERT INTO users (id, google_id, email, full_name, avatar_url, is_super_admin, created_at, updated_at)
                                        VALUES (@id, @gid, @email, @full_name, @avatar_url, @super, @now, @now)
                                        """
                                    |> Db.setTransaction tx
                                    |> Db.setParams
                                        [ "id", SqlType.String newId
                                          "gid", SqlType.String payload.GoogleSubject
                                          "email", SqlType.String normalizedEmail
                                          "full_name", SqlType.String payload.FullName
                                          "avatar_url",
                                          (match payload.AvatarUrl with
                                           | Some a -> SqlType.String a
                                           | None -> SqlType.Null)
                                          "super", SqlType.Int32 superFlag
                                          "now", SqlType.String nowIso ]
                                    |> Db.exec

                                    newId

                            conn
                            |> Db.newCommand
                                """
                                UPDATE custody_holders
                                SET user_id = @user_id, updated_at = @now
                                WHERE LOWER(invited_email) = @email
                                  AND user_id IS NULL
                                """
                            |> Db.setTransaction tx
                            |> Db.setParams
                                [ "user_id", SqlType.String userIdStr
                                  "now", SqlType.String nowIso
                                  "email", SqlType.String normalizedEmail ]
                            |> Db.exec

                            let userId = UserId(Guid.Parse(userIdStr))
                            let memberships = Auth.loadUserMemberships conn userId normalizedEmail

                            match memberships with
                            | [] -> return Error(ApiError.single ApiErrorCode.EmailNotWhitelisted)
                            | primaryMembership :: _ ->
                                let rawSessionToken = Auth.generateSecureSessionToken ()
                                let tokenHash = Auth.hashSessionToken rawSessionToken
                                let expiresIso = DateTimeOffset.UtcNow.AddDays(30.0).ToString("O")

                                conn
                                |> Db.newCommand
                                    """
                                    INSERT INTO user_sessions (token_hash, user_id, active_org_id, expires_at, created_at)
                                    VALUES (@hash, @user_id, @org_id, @expires, @now)
                                    """
                                |> Db.setTransaction tx
                                |> Db.setParams
                                    [ "hash", SqlType.String tokenHash
                                      "user_id", SqlType.String userIdStr
                                      "org_id", SqlType.String(primaryMembership.OrganizationId.ToDbString())
                                      "expires", SqlType.String expiresIso
                                      "now", SqlType.String nowIso ]
                                |> Db.exec

                                Auth.issueSessionCookie ctx rawSessionToken primaryMembership.OrganizationId
                                return Ok(userId, primaryMembership)
                    })

            let! redirectTarget = computeSmartRedirectAsync db activeMembership validReturnTo

            return
                { UserId = userId.ToDbString()
                  HolderId = activeMembership.HolderId.ToDbString()
                  ActiveOrganizationId = activeMembership.OrganizationId.ToDbString()
                  Role = int activeMembership.Role
                  RedirectTo = redirectTarget }
        }

    let getLoginBootstrapHandler (db: IDbContext) (devMode: bool) : EndpointHandler =
        Http.runWorkflow (fun ctx ->
            taskResult {
                let returnToParam = Http.tryGetQueryParam "return_to" ctx
                let! validReturnTo = validateReturnToPath returnToParam

                match Auth.authenticateRequest db devMode ctx with
                | Ok auth ->
                    let! target = computeSmartRedirectAsync db auth.ActiveOrg validReturnTo

                    return
                        { IsAuthenticated = true
                          RedirectTo = Some target
                          ReturnTo = validReturnTo }
                | Error _ ->
                    return
                        { IsAuthenticated = false
                          RedirectTo = None
                          ReturnTo = validReturnTo }
            })

    let postGoogleLoginHandler (db: IDbContext) (googleValidator: IGoogleTokenValidator) : EndpointHandler =
        Http.runWorkflow (fun ctx ->
            taskResult {
                let! req = Http.bindJsonSafe<GoogleLoginRequestDto> ctx

                let! idToken =
                    SmartConstructors.createNonEmptyString "id_token" req.IdToken
                    |> Result.mapError (fun e -> ApiError.fromFieldErrors [ e ])

                let! googleUser = googleValidator.ValidateIdTokenAsync idToken.Value
                return! completeIdentityLoginAsync db ctx googleUser None req.ReturnTo
            })

    let postDevLoginHandler (db: IDbContext) (devModeEnabled: bool) : EndpointHandler =
        Http.runWorkflow (fun ctx ->
            taskResult {
                if not devModeEnabled then
                    return! Error(ApiError.single ApiErrorCode.ForbiddenRole)
                else
                    let! req = Http.bindJsonSafe<DevLoginRequestDto> ctx

                    let! email =
                        SmartConstructors.createNonEmptyString "email" req.Email
                        |> Result.mapError (fun e -> ApiError.fromFieldErrors [ e ])

                    let fullName =
                        SmartConstructors.createOptionalTrimmedString req.FullName
                        |> Option.defaultValue "Dev User"

                    let mockPayload =
                        { GoogleSubject = "dev-sub-" + email.Value.ToLowerInvariant()
                          Email = email.Value.ToLowerInvariant()
                          FullName = fullName
                          AvatarUrl = None }

                    return! completeIdentityLoginAsync db ctx mockPayload req.IsSuperAdmin req.ReturnTo
            })

    let postLogoutHandler (db: IDbContext) : EndpointHandler =
        Http.runWorkflow (fun ctx ->
            taskResult {
                let cookieTokenOpt =
                    match ctx.Request.Cookies.TryGetValue(Auth.SessionCookieName) with
                    | true, raw -> ofNullableTrimmed raw
                    | false, _ -> None

                match cookieTokenOpt with
                | Some rawToken ->
                    let tokenHash = Auth.hashSessionToken rawToken

                    let! _ =
                        db.ExecuteWriteTransactionAsync(fun conn tx ->
                            task {
                                conn
                                |> Db.newCommand "DELETE FROM user_sessions WHERE token_hash = @hash"
                                |> Db.setTransaction tx
                                |> Db.setParams [ "hash", SqlType.String tokenHash ]
                                |> Db.exec

                                return Ok()
                            })

                    ctx.Response.Cookies.Delete(Auth.SessionCookieName)
                    return {| logged_out = true |}
                | None ->
                    ctx.Response.Cookies.Delete(Auth.SessionCookieName)
                    return {| logged_out = true |}
            })

    let endpoints (db: IDbContext) (googleValidator: IGoogleTokenValidator) (devModeEnabled: bool) : Endpoint list =
        [ GET [ route "/pages/login" (getLoginBootstrapHandler db devModeEnabled) ]
          POST
              [ route "/auth/google" (postGoogleLoginHandler db googleValidator)
                route "/auth/dev-login" (postDevLoginHandler db devModeEnabled)
                route "/auth/logout" (postLogoutHandler db) ] ]
