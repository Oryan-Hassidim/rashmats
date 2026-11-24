namespace Rashmatz.Shared

open System
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Data.Sqlite
open Donald
open FsToolkit.ErrorHandling

/// Global Navbar, X-Holders-Hash ETag caching, and Super-Admin organization creation.
/// Zero [<JsonPropertyName>] attributes — global SnakeCaseLower policy handles wire formatting.
module Navbar =

    [<Literal>]
    let HoldersHashHeader = "X-Holders-Hash"

    type HolderDirEntry =
        { Id: string; HolderType: int; Name: string; InternalIdentifier: string option
          DepartmentId: string option; DepartmentName: string option
          Role: int; AttendanceStatus: int; PhoneNumber: string option; IsActive: bool }

    type OrgSummary = { Id: string; Name: string; Slug: string; Role: int }
    type ActiveAuditBanner = { Id: string; Title: string; OpenedAt: string }

    type NavbarUser =
        { UserId: string; HolderId: string; FullName: string; Email: string
          AvatarUrl: string option; Role: int; AttendanceStatus: int; IsSuperAdmin: bool }

    type NavbarDto =
        { User: NavbarUser
          ActiveOrganization: OrgSummary
          AvailableOrganizations: OrgSummary list
          ActiveAuditSession: ActiveAuditBanner option
          HoldersDirectoryHash: string
          HoldersDirectory: HolderDirEntry list option }

    type PageEnvelope<'T> = { Navbar: NavbarDto; Page: 'T }

    [<CLIMutable>]
    type CreateOrgRequest =
        { Name: string; Slug: string
          FirstAdminName: string option; FirstAdminEmail: string option }

    type CreateOrgResponse = { OrganizationId: string; Slug: string; AdminHolderId: string }

    let private computeHash (entries: HolderDirEntry list) =
        let sb = StringBuilder()
        for h in entries do
            sb.Append($"{h.Id}|{h.HolderType}|{h.Name}|{h.InternalIdentifier}|{h.DepartmentId}|{h.DepartmentName}|{h.Role}|{h.AttendanceStatus}|{h.PhoneNumber}|{h.IsActive}\n") |> ignore
        SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))
        |> Convert.ToHexString
        |> fun s -> s.Substring(0, 16).ToLowerInvariant()

    let findActiveAudit (conn: SqliteConnection) (orgId: OrgId) =
        conn
        |> Db.newCommand "SELECT id, title, opened_at FROM audit_sessions WHERE organization_id=@org_id AND status=0 ORDER BY opened_at DESC LIMIT 1"
        |> Db.setParams [ "org_id", SqlType.String orgId.Str ]
        |> Db.querySingle (fun rd -> { Id = rd.ReadString "id"; Title = rd.ReadString "title"; OpenedAt = rd.ReadString "opened_at" })

    let build (db: IDbContext) (ctx: HttpContext) (auth: AuthContext) : NavbarDto =
        use conn = db.OpenRead()
        let entries =
            conn
            |> Db.newCommand
                """
                SELECT ch.id, ch.holder_type, ch.name, ch.internal_identifier,
                       ch.department_id, d.name AS department_name, ch.role,
                       ch.attendance_status, ch.phone_number, ch.is_active
                FROM custody_holders ch
                LEFT JOIN departments d ON d.id = ch.department_id
                WHERE ch.organization_id = @org_id
                ORDER BY ch.name ASC, ch.id ASC
                """
            |> Db.setParams [ "org_id", SqlType.String auth.ActiveOrg.OrgId.Str ]
            |> Db.query (fun rd ->
                { Id = rd.ReadString "id"; HolderType = rd.ReadInt32 "holder_type"; Name = rd.ReadString "name"
                  InternalIdentifier = rd.ReadStringOption "internal_identifier"
                  DepartmentId = rd.ReadStringOption "department_id"; DepartmentName = rd.ReadStringOption "department_name"
                  Role = rd.ReadInt32 "role"; AttendanceStatus = rd.ReadInt32 "attendance_status"
                  PhoneNumber = rd.ReadStringOption "phone_number"; IsActive = rd.ReadInt32 "is_active" = 1 })

        let hash = computeHash entries
        let clientHash =
            match ctx.Request.Headers.TryGetValue HoldersHashHeader with
            | true, v when v.Count > 0 -> trimNullable v[0]
            | _ -> None

        let directory =
            match clientHash with
            | Some h when String.Equals(h, hash, StringComparison.OrdinalIgnoreCase) -> None
            | _ -> Some entries

        { User =
            { UserId = auth.UserId.Value; HolderId = auth.ActiveOrg.HolderId.Str
              FullName = auth.FullName; Email = auth.Email; AvatarUrl = auth.AvatarUrl
              Role = int auth.ActiveOrg.Role; AttendanceStatus = int auth.ActiveOrg.AttendanceStatus
              IsSuperAdmin = auth.IsSuperAdmin }
          ActiveOrganization =
            { Id = auth.ActiveOrg.OrgId.Str; Name = auth.ActiveOrg.OrgName
              Slug = auth.ActiveOrg.OrgSlug; Role = int auth.ActiveOrg.Role }
          AvailableOrganizations =
            auth.AvailableOrgs |> List.map (fun m -> { Id = m.OrgId.Str; Name = m.OrgName; Slug = m.OrgSlug; Role = int m.Role })
          ActiveAuditSession = findActiveAudit conn auth.ActiveOrg.OrgId
          HoldersDirectoryHash = hash
          HoldersDirectory = directory }

    let createOrganization (db: IDbContext) (auth: AuthContext) (req: CreateOrgRequest) : Task<Result<CreateOrgResponse, ApiError>> =
        taskResult {
            let! name, slug =
                validation {
                    let! n = Some req.Name |> Parse.nonEmpty "name" 
                    and! s = Some req.Slug |>Parse.nonEmpty "slug" 
                    return n.Value, s.Value.ToLowerInvariant()
                }
                |> Result.mapError ApiError.fromFields

            let adminEmail = trimOpt req.FirstAdminEmail |> Option.map _.ToLowerInvariant() |> Option.defaultValue (auth.Email.ToLowerInvariant())
            let adminName = trimOpt req.FirstAdminName |> Option.defaultValue auth.FullName
            let creatorUid = if String.Equals(adminEmail, auth.Email, StringComparison.OrdinalIgnoreCase) then Some auth.UserId.Value else None

            return! db.WriteTxAsync(fun conn tx ->
                task {
                    let existing =
                        conn |> Db.newCommand "SELECT id FROM organizations WHERE LOWER(slug)=@slug"
                        |> Db.setTransaction tx |> Db.setParams [ "slug", SqlType.String slug ]
                        |> Db.querySingle (fun rd -> rd.ReadString "id")

                    match existing with
                    | Some id -> return Error(ApiError.withTarget ApiErrorCode.OrganizationSlugAlreadyExists id)
                    | None ->
                        let orgId, holderId, now = Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow.ToString("O")
                        conn |> Db.newCommand "INSERT INTO organizations (id, name, slug, is_active, created_at, updated_at) VALUES (@id, @name, @slug, 1, @now, @now)"
                        |> Db.setTransaction tx |> Db.setParams [ "id", SqlType.String orgId; "name", SqlType.String name; "slug", SqlType.String slug; "now", SqlType.String now ] |> Db.exec

                        conn |> Db.newCommand "INSERT INTO custody_holders (id, organization_id, user_id, holder_type, name, invited_email, role, attendance_status, is_active, created_at, updated_at) VALUES (@id, @org, @uid, 1, @name, @email, 2, 1, 1, @now, @now)"
                        |> Db.setTransaction tx |> Db.setParams [ "id", SqlType.String holderId; "org", SqlType.String orgId; "uid", Sql.strOpt creatorUid; "name", SqlType.String adminName; "email", SqlType.String adminEmail; "now", SqlType.String now ] |> Db.exec

                        return Ok { OrganizationId = orgId; Slug = slug; AdminHolderId = holderId }
                })
        }
