module MockDb

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Auth

type MockOrganization = {
    Id: OrgId
    Slug: string
    Name: string
    IsActive: bool
}

type MockWhitelistEntry = {
    HolderId: HolderId
    OrgId: OrgId
    Email: string
    FullName: string
    Role: TenantRole
    IsActive: bool
}

type MockUser = {
    Id: UserId
    GoogleSub: GoogleSub
    Email: EmailAddress
    FullName: FullName
    IsSuperAdmin: bool
}

type MockAuthStore(config: IConfiguration) =
    let orgsById = ConcurrentDictionary<Guid, MockOrganization>()
    let orgsBySlug = ConcurrentDictionary<string, MockOrganization>(StringComparer.OrdinalIgnoreCase)
    let whitelist = ConcurrentDictionary<string, MockWhitelistEntry list>(StringComparer.OrdinalIgnoreCase)
    let usersByGoogleSub = ConcurrentDictionary<string, MockUser>()
    let usersById = ConcurrentDictionary<Guid, MockUser>()
    let superAdminEmails = ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase)

    let addOrg (idStr: string) (slug: string) (name: string) (isActive: bool) =
        let org = {
            Id = OrgId.fromGuid (Guid.Parse idStr)
            Slug = slug
            Name = name
            IsActive = isActive
        }
        orgsById[OrgId.value org.Id] <- org
        orgsBySlug[slug] <- org
        org

    let addWhitelistEntry (org: MockOrganization) (email: string) (fullName: string) (role: TenantRole) (isActive: bool) =
        let normalized = email.Trim().ToLowerInvariant()
        let entry = {
            HolderId = HolderId.create ()
            OrgId = org.Id
            Email = normalized
            FullName = fullName
            Role = role
            IsActive = isActive
        }
        whitelist.AddOrUpdate(
            normalized,
            [ entry ],
            fun _ existing ->
                let others = existing |> List.filter (fun e -> OrgId.value e.OrgId <> OrgId.value org.Id)
                others @ [ entry ]
        )
        |> ignore

    do
        let hqOrg = addOrg "11111111-1111-4111-8111-111111111111" "rashmatz-hq" "Rashmatz HQ" true
        let fieldOrg = addOrg "22222222-2222-4222-8222-222222222222" "field-ops" "Field Operations" true
        let archivedOrg = addOrg "33333333-3333-4333-8333-333333333333" "archived-org" "Archived Unit" false

        // Default dev accounts matching rashmatz.http
        addWhitelistEntry hqOrg "admin@rashmatz.org" "HQ Admin" Admin true
        addWhitelistEntry fieldOrg "admin@rashmatz.org" "HQ Admin" Member true
        addWhitelistEntry hqOrg "soldier@rashmatz.org" "Field Member" Member true
        addWhitelistEntry hqOrg "member@rashmatz.org" "Standard Member" Member true
        addWhitelistEntry archivedOrg "inactive@rashmatz.org" "Archived Member" Member true

        // Optional entries configured in appsettings.json -> Authentication:SeedWhitelist
        let seedSection = config.GetSection("Authentication:SeedWhitelist")
        for child in seedSection.GetChildren() do
            let emailOpt =
                child["Email"]
                |> Option.ofObj
                |> Option.map _.Trim()
                |> Option.filter (String.IsNullOrWhiteSpace >> not)

            match emailOpt with
            | None -> ()
            | Some email ->
                let slug =
                    child["OrgSlug"]
                    |> Option.ofObj
                    |> Option.map _.Trim()
                    |> Option.filter (String.IsNullOrWhiteSpace >> not)
                    |> Option.defaultValue hqOrg.Slug

                let org =
                    match orgsBySlug.TryGetValue(slug) with
                    | true, existing -> existing
                    | false, _ -> addOrg (Guid.CreateVersion7().ToString("D")) slug slug true

                let role =
                    child["Role"]
                    |> Option.ofObj
                    |> Option.bind TenantRole.tryParse
                    |> Option.defaultValue Member

                let fullName =
                    child["FullName"]
                    |> Option.ofObj
                    |> Option.map _.Trim()
                    |> Option.filter (String.IsNullOrWhiteSpace >> not)
                    |> Option.defaultValue email

                addWhitelistEntry org email fullName role true

                match Boolean.TryParse(child["IsSuperAdmin"]) with
                | true, true -> superAdminEmails[email.ToLowerInvariant()] <- true
                | _ -> ()

    let buildMemberships (email: string) : OrgMembership list =
        match whitelist.TryGetValue(email) with
        | false, _ -> []
        | true, entries ->
            entries
            |> List.choose (fun entry ->
                match orgsById.TryGetValue(OrgId.value entry.OrgId) with
                | false, _ -> None
                | true, org ->
                    Some {
                        OrgId = org.Id
                        OrgSlug = org.Slug
                        OrgName = org.Name
                        OrgIsActive = org.IsActive
                        HolderId = entry.HolderId
                        HolderIsActive = entry.IsActive
                        Role = entry.Role
                    })

    /// In-memory mock implementation of UpsertGoogleUser enforcing email whitelist and org status.
    member _.UpsertGoogleUser : UpsertGoogleUser =
        fun userInfo ->
            let email = EmailAddress.value userInfo.Email
            let memberships = buildMemberships email

            if List.isEmpty memberships then
                Task.FromResult(Error(EmailNotWhitelisted email))
            elif not (memberships |> List.exists (fun m -> m.OrgIsActive && m.HolderIsActive)) then
                Task.FromResult(Error OrganizationInactive)
            else
                let key = GoogleSub.value userInfo.GoogleSub
                let isSuper = superAdminEmails.ContainsKey(email)

                let stored =
                    usersByGoogleSub.AddOrUpdate(
                        key,
                        (fun _ -> {
                            Id = UserId.create ()
                            GoogleSub = userInfo.GoogleSub
                            Email = userInfo.Email
                            FullName = userInfo.FullName
                            IsSuperAdmin = isSuper
                        }),
                        (fun _ existing -> {
                            existing with
                                Email = userInfo.Email
                                FullName = userInfo.FullName
                                IsSuperAdmin = isSuper
                        })
                    )

                usersById[UserId.value stored.Id] <- stored
                Task.FromResult(Ok stored.Id)

    /// In-memory mock implementation of ResolveUserAuthContext for Member/Admin tenant authorization.
    member _.ResolveUserAuthContext : ResolveUserAuthContext =
        fun userId ->
            match usersById.TryGetValue(UserId.value userId) with
            | false, _ -> Task.FromResult(Error MissingPrincipal)
            | true, user ->
                let email = EmailAddress.value user.Email
                let memberships = buildMemberships email

                if List.isEmpty memberships then
                    Task.FromResult(Error(EmailNotWhitelisted email))
                elif not (memberships |> List.exists (fun m -> m.OrgIsActive && m.HolderIsActive)) then
                    Task.FromResult(Error OrganizationInactive)
                else
                    Task.FromResult(
                        Ok {
                            UserId = user.Id
                            Email = user.Email
                            FullName = user.FullName
                            IsSuperAdmin = user.IsSuperAdmin
                            Memberships = memberships
                        }
                    )
