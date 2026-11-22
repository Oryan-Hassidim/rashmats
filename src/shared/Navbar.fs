namespace Rashmatz.Shared

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json.Serialization
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Donald
open FsToolkit.ErrorHandling
open Rashmatz.Shared.DomainTypes
open Rashmatz.Shared.Schema

/// Shared Navbar & Holders Directory Caching (Section 1.3 & Section 2 of pages_and_components_spec.md):
/// - Computes deterministic SHA-256 hash over the organization's `custody_holders` directory.
/// - If the client's `X-Holders-Hash` header matches, returns `holders_directory = None` (saving ~90% payload size).
/// - Also provides `POST /organizations` for Super-Admins (Section 2.2).
module Navbar =

    [<Literal>]
    let HoldersHashHeaderName = "X-Holders-Hash"

    type HolderDirectoryEntryDto =
        { [<JsonPropertyName("id")>]
          Id: string
          [<JsonPropertyName("holder_type")>]
          HolderType: int
          [<JsonPropertyName("name")>]
          Name: string
          [<JsonPropertyName("internal_identifier")>]
          InternalIdentifier: string option
          [<JsonPropertyName("department_id")>]
          DepartmentId: string option
          [<JsonPropertyName("department_name")>]
          DepartmentName: string option
          [<JsonPropertyName("role")>]
          Role: int
          [<JsonPropertyName("attendance_status")>]
          AttendanceStatus: int
          [<JsonPropertyName("phone_number")>]
          PhoneNumber: string option
          [<JsonPropertyName("is_active")>]
          IsActive: bool }

    type AvailableOrganizationDto =
        { [<JsonPropertyName("id")>]
          Id: string
          [<JsonPropertyName("name")>]
          Name: string
          [<JsonPropertyName("slug")>]
          Slug: string
          [<JsonPropertyName("role")>]
          Role: int }

    type ActiveAuditBannerDto =
        { [<JsonPropertyName("id")>]
          Id: string
          [<JsonPropertyName("title")>]
          Title: string
          [<JsonPropertyName("opened_at")>]
          OpenedAt: string }

    type NavbarUserDto =
        { [<JsonPropertyName("user_id")>]
          UserId: string
          [<JsonPropertyName("holder_id")>]
          HolderId: string
          [<JsonPropertyName("full_name")>]
          FullName: string
          [<JsonPropertyName("email")>]
          Email: string
          [<JsonPropertyName("avatar_url")>]
          AvatarUrl: string option
          [<JsonPropertyName("role")>]
          Role: int
          [<JsonPropertyName("attendance_status")>]
          AttendanceStatus: int
          [<JsonPropertyName("is_super_admin")>]
          IsSuperAdmin: bool }

    type NavbarDto =
        { [<JsonPropertyName("user")>]
          User: NavbarUserDto
          [<JsonPropertyName("active_organization")>]
          ActiveOrganization: AvailableOrganizationDto
          [<JsonPropertyName("available_organizations")>]
          AvailableOrganizations: AvailableOrganizationDto list
          [<JsonPropertyName("active_audit_session")>]
          ActiveAuditSession: ActiveAuditBannerDto option
          [<JsonPropertyName("holders_directory_hash")>]
          HoldersDirectoryHash: string
          [<JsonPropertyName("holders_directory")>]
          HoldersDirectory: HolderDirectoryEntryDto list option }

    /// Standard Bootstrap envelope returned by every authenticated `GET /pages/...` endpoint.
    type BootstrapEnvelopeDto<'TPage> =
        { [<JsonPropertyName("navbar")>]
          Navbar: NavbarDto
          [<JsonPropertyName("page")>]
          Page: 'TPage }

    let private computeHoldersHash (entries: HolderDirectoryEntryDto list) : string =
        let sb = StringBuilder()

        for h in entries do
            sb
                .Append(h.Id)
                .Append('|')
                .Append(h.HolderType)
                .Append('|')
                .Append(h.Name)
                .Append('|')
                .Append(Option.defaultValue "" h.InternalIdentifier)
                .Append('|')
                .Append(Option.defaultValue "" h.DepartmentId)
                .Append('|')
                .Append(Option.defaultValue "" h.DepartmentName)
                .Append('|')
                .Append(h.Role)
                .Append('|')
                .Append(h.AttendanceStatus)
                .Append('|')
                .Append(Option.defaultValue "" h.PhoneNumber)
                .Append('|')
                .Append(if h.IsActive then '1' else '0')
                .Append('\n')
            |> ignore

        let hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))
        Convert.ToHexString(hashBytes).Substring(0, 16).ToLowerInvariant()

    let loadHoldersDirectory
        (conn: Microsoft.Data.Sqlite.SqliteConnection)
        (orgId: OrgId)
        : HolderDirectoryEntryDto list * string =
        let entries =
            conn
            |> Db.newCommand
                """
                SELECT
                    ch.id,
                    ch.holder_type,
                    ch.name,
                    ch.internal_identifier,
                    ch.department_id,
                    d.name AS department_name,
                    ch.role,
                    ch.attendance_status,
                    ch.phone_number,
                    ch.is_active
                FROM custody_holders ch
                LEFT JOIN departments d ON d.id = ch.department_id
                WHERE ch.organization_id = @org_id
                ORDER BY ch.name ASC, ch.id ASC
                """
            |> Db.setParams [ "org_id", SqlType.String(orgId.ToDbString()) ]
            |> Db.query (fun rd ->
                { Id = rd.ReadString "id"
                  HolderType = rd.ReadInt32 "holder_type"
                  Name = rd.ReadString "name"
                  InternalIdentifier = rd.ReadStringOption "internal_identifier"
                  DepartmentId = rd.ReadStringOption "department_id"
                  DepartmentName = rd.ReadStringOption "department_name"
                  Role = rd.ReadInt32 "role"
                  AttendanceStatus = rd.ReadInt32 "attendance_status"
                  PhoneNumber = rd.ReadStringOption "phone_number"
                  IsActive = rd.ReadInt32 "is_active" = 1 })

        let hash = computeHoldersHash entries
        entries, hash

    let findActiveAuditSession
        (conn: Microsoft.Data.Sqlite.SqliteConnection)
        (orgId: OrgId)
        : ActiveAuditBannerDto option =
        let orgIdStr = orgId.ToDbString()
        let openStatus = int AuditSessionStatus.Open

        conn
        |> Db.newCommand
            """
            SELECT id, organization_id, title, initiated_by_holder_id, status, opened_at, closed_at, updated_at
            FROM audit_sessions
            WHERE organization_id = @org_id AND status = @status
            ORDER BY opened_at DESC
            LIMIT 1
            """
        |> Db.setParams
            [ "org_id", SqlType.String orgIdStr
              "status", SqlType.Int32 openStatus ]
        |> Db.querySingle main.Readers.read_audit_sessions
        |> Option.map (fun (s: main.audit_sessions) ->
            { Id = s.id
              Title = s.title
              OpenedAt = s.opened_at })

    let buildNavbarAsync
        (db: IDbContext)
        (ctx: HttpContext)
        (auth: AuthenticatedContext)
        : Task<NavbarDto> =
        task {
            use conn = db.OpenReadConnection()

            let entries, currentHash = loadHoldersDirectory conn auth.ActiveOrg.OrganizationId
            let activeAuditOpt = findActiveAuditSession conn auth.ActiveOrg.OrganizationId

            let clientHashOpt =
                match ctx.Request.Headers.TryGetValue(HoldersHashHeaderName) with
                | true, v when v.Count > 0 -> ofNullableTrimmed v[0]
                | _ -> None

            let directoryPayload =
                match clientHashOpt with
                | Some clientHash when String.Equals(clientHash, currentHash, StringComparison.OrdinalIgnoreCase) ->
                    None
                | _ -> Some entries

            let activeOrgDto =
                { Id = auth.ActiveOrg.OrganizationId.ToDbString()
                  Name = auth.ActiveOrg.OrganizationName
                  Slug = auth.ActiveOrg.OrganizationSlug
                  Role = int auth.ActiveOrg.Role }

            let availableOrgsDto =
                auth.AvailableOrgs
                |> List.map (fun m ->
                    { Id = m.OrganizationId.ToDbString()
                      Name = m.OrganizationName
                      Slug = m.OrganizationSlug
                      Role = int m.Role })

            return
                { User =
                    { UserId = auth.UserId.ToDbString()
                      HolderId = auth.ActiveOrg.HolderId.ToDbString()
                      FullName = auth.FullName
                      Email = auth.Email
                      AvatarUrl = auth.AvatarUrl
                      Role = int auth.ActiveOrg.Role
                      AttendanceStatus = int auth.ActiveOrg.AttendanceStatus
                      IsSuperAdmin = auth.IsSuperAdmin }
                  ActiveOrganization = activeOrgDto
                  AvailableOrganizations = availableOrgsDto
                  ActiveAuditSession = activeAuditOpt
                  HoldersDirectoryHash = currentHash
                  HoldersDirectory = directoryPayload }
        }

    // Super-Admin Create New Organization (`POST /organizations` in Section 2.2)
    [<CLIMutable>]
    type CreateOrganizationRequestDto =
        { [<JsonPropertyName("name")>]
          Name: string option
          [<JsonPropertyName("slug")>]
          Slug: string option
          [<JsonPropertyName("first_admin_name")>]
          FirstAdminName: string option
          [<JsonPropertyName("first_admin_email")>]
          FirstAdminEmail: string option }

    type CreateOrganizationResponseDto =
        { [<JsonPropertyName("organization_id")>]
          OrganizationId: string
          [<JsonPropertyName("slug")>]
          Slug: string
          [<JsonPropertyName("admin_holder_id")>]
          AdminHolderId: string }

    type ValidatedCreateOrganization =
        { Name: NonEmptyString
          Slug: NonEmptyString
          FirstAdminName: string
          FirstAdminEmail: string
          LinkToCreatorUserId: UserId option }

    let validateCreateOrganization
        (auth: AuthenticatedContext)
        (req: CreateOrganizationRequestDto)
        : Result<ValidatedCreateOrganization, ApiError> =
        validation {
            let! name = SmartConstructors.createNonEmptyString "name" req.Name
            and! slug = SmartConstructors.createNonEmptyString "slug" req.Slug

            let adminEmail =
                SmartConstructors.createOptionalTrimmedString req.FirstAdminEmail
                |> Option.map (fun s -> s.ToLowerInvariant())
                |> Option.defaultValue (auth.Email.ToLowerInvariant())

            let adminName =
                SmartConstructors.createOptionalTrimmedString req.FirstAdminName
                |> Option.defaultValue auth.FullName

            let linkUserId =
                if String.Equals(adminEmail, auth.Email, StringComparison.OrdinalIgnoreCase) then
                    Some auth.UserId
                else
                    None

            return
                { Name = name
                  Slug = slug
                  FirstAdminName = adminName
                  FirstAdminEmail = adminEmail
                  LinkToCreatorUserId = linkUserId }
        }
        |> Result.mapError ApiError.fromFieldErrors

    let createOrganizationWorkflow
        (db: IDbContext)
        (auth: AuthenticatedContext)
        (req: CreateOrganizationRequestDto)
        : Task<Result<CreateOrganizationResponseDto, ApiError>> =
        taskResult {
            let! cmd = validateCreateOrganization auth req
            let slugLower = cmd.Slug.Value.ToLowerInvariant()

            return!
                db.ExecuteWriteTransactionAsync(fun conn tx ->
                    task {
                        let existingOpt =
                            conn
                            |> Db.newCommand "SELECT id FROM organizations WHERE LOWER(slug) = @slug"
                            |> Db.setTransaction tx
                            |> Db.setParams [ "slug", SqlType.String slugLower ]
                            |> Db.querySingle (fun rd -> rd.ReadString "id")

                        match existingOpt with
                        | Some existingId ->
                            return Error(ApiError.withTarget ApiErrorCode.OrganizationSlugAlreadyExists existingId)
                        | None ->
                            let newOrgId = Guid.NewGuid().ToString("D")
                            let newHolderId = Guid.NewGuid().ToString("D")
                            let nowIso = DateTimeOffset.UtcNow.ToString("O")

                            conn
                            |> Db.newCommand
                                """
                                INSERT INTO organizations (id, name, slug, is_active, created_at, updated_at)
                                VALUES (@id, @name, @slug, 1, @now, @now)
                                """
                            |> Db.setTransaction tx
                            |> Db.setParams
                                [ "id", SqlType.String newOrgId
                                  "name", SqlType.String cmd.Name.Value
                                  "slug", SqlType.String slugLower
                                  "now", SqlType.String nowIso ]
                            |> Db.exec

                            conn
                            |> Db.newCommand
                                """
                                INSERT INTO custody_holders (
                                    id, organization_id, user_id, holder_type, name,
                                    invited_email, role, attendance_status, is_active, created_at, updated_at
                                )
                                VALUES (
                                    @id, @org_id, @user_id, 1, @name,
                                    @email, 2, 1, 1, @now, @now
                                )
                                """
                            |> Db.setTransaction tx
                            |> Db.setParams
                                [ "id", SqlType.String newHolderId
                                  "org_id", SqlType.String newOrgId
                                  "user_id",
                                  (match cmd.LinkToCreatorUserId with
                                   | Some u -> SqlType.String(u.ToDbString())
                                   | None -> SqlType.Null)
                                  "name", SqlType.String cmd.FirstAdminName
                                  "email", SqlType.String cmd.FirstAdminEmail
                                  "now", SqlType.String nowIso ]
                            |> Db.exec

                            return
                                Ok
                                    { OrganizationId = newOrgId
                                      Slug = slugLower
                                      AdminHolderId = newHolderId }
                    })
        }
