namespace Rashmatz.Pages.Login

open System
open System.ComponentModel.DataAnnotations
open Microsoft.AspNetCore.Http
open Microsoft.Data.Sqlite
open Oxpecker
open Donald
open FsToolkit.ErrorHandling
open Rashmatz.Shared

/// Vertical Slice — Page 1: Login & Smart Routing (`/login`)
module Server =
    open Rashmatz.Shared.Navbar

    type LoginBootstrapDto =
        { IsAuthenticated: bool
          RedirectTo: string option
          ReturnTo: string option }

    [<CLIMutable>]
    type GoogleLoginRequest =
        { [<Required; MinLength(1)>]
          IdToken: string
          ReturnTo: string option }

    [<CLIMutable>]
    type DevLoginRequest =
        { [<Required; MinLength(1)>]
          Email: string
          FullName: string option
          IsSuperAdmin: bool option
          ReturnTo: string option }

    type LoginResponse =
        { UserId: string
          HolderId: string
          ActiveOrganizationId: string
          Role: int
          RedirectTo: string }

    type LogoutResponse = { LoggedOut: bool }

    let validateReturnTo (rawOpt: string option) =
        match trimOpt rawOpt with
        | None -> Ok None
        | Some s when
            s.StartsWith "/"
            && not (s.StartsWith "//")
            && not (s.StartsWith "/\\")
            && not (s.StartsWith("/login", StringComparison.OrdinalIgnoreCase))
            ->
            Ok(Some s)
        | _ -> Error(ApiError.single ApiErrorCode.InvalidReturnToPath)

    let smartRedirect (db: IDbContext) (org: OrgMembership) (returnTo: string option) =
        match returnTo with
        | Some path -> path
        | None ->
            use conn = db.OpenRead()

            match org.Role, Navbar.findActiveAudit conn org.OrgId with
            | SystemRole.Member, Some _ -> "/audit/scan"
            | SystemRole.Member, None -> "/my-custody"
            | SystemRole.Admin, _ -> "/admin/dashboard"
            | _ -> "/my-custody"

    let private checkWhitelist (conn: SqliteConnection) tx (email: string) =
        let rows =
            conn
            |> Db.newCommand
                "SELECT o.is_active AS oa, ch.is_active AS ha, ch.role FROM custody_holders ch INNER JOIN organizations o ON o.id=ch.organization_id WHERE LOWER(ch.invited_email)=@email AND ch.holder_type=1"
            |> Db.setTransaction tx
            |> Db.setParams [ "email", SqlType.String email ]
            |> Db.query (fun rd -> rd.ReadInt32 "oa" = 1, rd.ReadInt32 "ha" = 1, rd.ReadInt32 "role")

        if rows
           |> List.exists (fun (oa, ha, r) -> oa && ha && r > 0) then
            Ok()
        elif rows |> List.exists (fun (oa, _, _) -> not oa) then
            Error(ApiError.single ApiErrorCode.OrganizationInactive)
        else
            Error(ApiError.single ApiErrorCode.EmailNotWhitelisted)

    let private upsertUser (conn: SqliteConnection) tx (user: GoogleUser) (forceSuper: bool option) (now: string) =
        let existing =
            conn
            |> Db.newCommand "SELECT id, is_super_admin FROM users WHERE LOWER(email)=@em OR google_id=@gid"
            |> Db.setTransaction tx
            |> Db.setParams [ "em", SqlType.String user.Email
                              "gid", SqlType.String user.Subject ]
            |> Db.querySingle (fun rd -> rd.ReadString "id", rd.ReadInt32 "is_super_admin" = 1)

        let uid =
            match existing with
            | Some (id, wasSuper) ->
                let sa =
                    if (forceSuper |> Option.defaultValue wasSuper) then
                        1
                    else
                        0

                conn
                |> Db.newCommand
                    "UPDATE users SET google_id=@gid, email=@em, full_name=@fn, avatar_url=@av, is_super_admin=@sa, updated_at=@now WHERE id=@id"
                |> Db.setTransaction tx
                |> Db.setParams [ "gid", SqlType.String user.Subject
                                  "em", SqlType.String user.Email
                                  "fn", SqlType.String user.FullName
                                  "av", Sql.strOpt user.AvatarUrl
                                  "sa", SqlType.Int32 sa
                                  "now", SqlType.String now
                                  "id", SqlType.String id ]
                |> Db.exec

                id
            | None ->
                let id = Guid.NewGuid().ToString("D")
                let sa = if forceSuper = Some true then 1 else 0

                conn
                |> Db.newCommand
                    "INSERT INTO users (id, google_id, email, full_name, avatar_url, is_super_admin, created_at, updated_at) VALUES (@id, @gid, @em, @fn, @av, @sa, @now, @now)"
                |> Db.setTransaction tx
                |> Db.setParams [ "id", SqlType.String id
                                  "gid", SqlType.String user.Subject
                                  "em", SqlType.String user.Email
                                  "fn", SqlType.String user.FullName
                                  "av", Sql.strOpt user.AvatarUrl
                                  "sa", SqlType.Int32 sa
                                  "now", SqlType.String now ]
                |> Db.exec

                id

        conn
        |> Db.newCommand
            "UPDATE custody_holders SET user_id=@uid, updated_at=@now WHERE LOWER(invited_email)=@em AND user_id IS NULL"
        |> Db.setTransaction tx
        |> Db.setParams [ "uid", SqlType.String uid
                          "now", SqlType.String now
                          "em", SqlType.String user.Email ]
        |> Db.exec

        UserId(Guid.Parse uid)

    let private completeLogin (db: IDbContext) ctx (user: GoogleUser) forceSuper returnToRaw =
        taskResult {
            let! validReturnTo = validateReturnTo returnToRaw
            let normalized = { user with Email = user.Email.Trim().ToLowerInvariant() }

            let! (userId: UserId, activeOrg: OrgMembership) =
                db.WriteTxAsync (fun conn tx ->
                    task {
                        match checkWhitelist conn tx normalized.Email with
                        | Error e -> return Error e
                        | Ok () ->
                            let now = DateTimeOffset.UtcNow.ToString("O")
                            let uid = upsertUser conn tx normalized forceSuper now

                            match Auth.loadMemberships conn uid normalized.Email with
                            | [] -> return Error(ApiError.single ApiErrorCode.EmailNotWhitelisted)
                            | primary :: _ ->
                                let rawToken = Auth.newSessionToken ()
                                let expires = DateTimeOffset.UtcNow.AddDays(30.0).ToString("O")

                                conn
                                |> Db.newCommand
                                    "INSERT INTO user_sessions (token_hash, user_id, active_org_id, expires_at, created_at) VALUES (@h, @uid, @oid, @exp, @now)"
                                |> Db.setTransaction tx
                                |> Db.setParams [ "h", SqlType.String(Auth.hashToken rawToken)
                                                  "uid", SqlType.String uid.Str
                                                  "oid", SqlType.String primary.OrgId.Str
                                                  "exp", SqlType.String expires
                                                  "now", SqlType.String now ]
                                |> Db.exec

                                Auth.issueCookies ctx rawToken primary.OrgId
                                return Ok(uid, primary)
                    })

            return
                { UserId = userId.Str
                  HolderId = activeOrg.HolderId.Str
                  ActiveOrganizationId = activeOrg.OrgId.Str
                  Role = int activeOrg.Role
                  RedirectTo = smartRedirect db activeOrg validReturnTo }
        }

    let endpoints (db: IDbContext) (googleValidator: IGoogleTokenValidator) (devMode: bool) : Endpoint list =
        [ GET [ route
                    "/pages/login"
                    (Http.runWorkflow (fun ctx ->
                        taskResult {
                            let! validReturnTo = validateReturnTo (Http.queryParam "return_to" ctx)

                            match Auth.authenticate db devMode ctx with
                            | Ok auth ->
                                return
                                    { IsAuthenticated = true
                                      RedirectTo = Some(smartRedirect db auth.ActiveOrg validReturnTo)
                                      ReturnTo = validReturnTo }
                            | Error _ ->
                                return
                                    { IsAuthenticated = false
                                      RedirectTo = None
                                      ReturnTo = validReturnTo }
                        }))
                |> Http.openApiGet<LoginBootstrapDto> [ 422 ] ]
          POST [ route
                     "/auth/google"
                     (Http.runWorkflow (fun ctx ->
                         taskResult {
                             let! req = Http.bindJson<GoogleLoginRequest> ctx

                             let! token =
                                 Parse.nonEmpty "id_token" (Some req.IdToken)
                                 |> Result.mapError (fun e -> ApiError.fromFields [ e ])

                             let! googleUser = googleValidator.ValidateAsync token.Value
                             return! completeLogin db ctx googleUser None req.ReturnTo
                         }))
                 |> Http.openApiPost<GoogleLoginRequest, LoginResponse> [ 401; 403; 422 ]

                 route
                     "/auth/dev-login"
                     (Http.runWorkflow (fun ctx ->
                         taskResult {
                             if not devMode then
                                 return! Error(ApiError.single ApiErrorCode.ForbiddenRole)
                             else
                                 let! req = Http.bindJson<DevLoginRequest> ctx

                                 let! email =
                                     Parse.nonEmpty "email" (Some req.Email)
                                     |> Result.mapError (fun e -> ApiError.fromFields [ e ])

                                 let user =
                                     { Subject = "dev-sub-" + email.Value.ToLowerInvariant()
                                       Email = email.Value
                                       FullName =
                                         trimOpt req.FullName
                                         |> Option.defaultValue "Dev User"
                                       AvatarUrl = None }

                                 return! completeLogin db ctx user req.IsSuperAdmin req.ReturnTo
                         }))
                 |> Http.openApiPost<DevLoginRequest, LoginResponse> [ 403; 422 ]

                 route
                     "/auth/logout"
                     (Http.runWorkflow (fun ctx ->
                         taskResult {
                             match ctx.Request.Cookies.TryGetValue Auth.SessionCookie with
                             | true, raw ->
                                 match trimNullable raw with
                                 | Some token ->
                                     let! _ =
                                         db.WriteTxAsync (fun conn tx ->
                                             task {
                                                 conn
                                                 |> Db.newCommand "DELETE FROM user_sessions WHERE token_hash=@h"
                                                 |> Db.setTransaction tx
                                                 |> Db.setParams [ "h", SqlType.String(Auth.hashToken token) ]
                                                 |> Db.exec

                                                 return Ok()
                                             })

                                     ()
                                 | None -> ()
                             | _ -> ()

                             ctx.Response.Cookies.Delete Auth.SessionCookie
                             return { LoggedOut = true }
                         }))
                 |> Http.openApiPost<unit, LogoutResponse> [] ] ]
