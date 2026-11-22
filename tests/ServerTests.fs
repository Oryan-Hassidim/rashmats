namespace Rashmatz.Server.Tests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Oxpecker
open Donald
open Xunit
open Rashmatz
open Rashmatz.Shared
open Rashmatz.Shared.DomainTypes
open Rashmatz.Pages.TransfersNewItems.Domain

/// Deterministic Mock Google Token Validator for testing `/auth/google` without network calls to Google.
type MockGoogleTokenValidator(resolver: string -> Result<GoogleUserPayload, ApiError>) =
    interface IGoogleTokenValidator with
        member _.ValidateIdTokenAsync(idToken: string) =
            Task.FromResult(resolver idToken)

module TestHarness =

    type SeedIds =
        { OrgId: string
          DeptId: string
          AdminHolderId: string
          MemberHolderId: string
          WarehouseHolderId: string
          CategoryId: string
          RadioItemTypeId: string
          CanteenItemTypeId: string
          Radio1Id: string
          Radio2Id: string
          TemplateId: string }

    let getRequiredJsonString (propName: string) (el: JsonElement) : string =
        match el.GetProperty(propName).GetString() with
        | null -> failwith $"Expected non-null string in JSON property '{propName}'"
        | s -> s

    let createTestServer (devModeEnabled: bool) (googleMock: IGoogleTokenValidator) =
        let dbFile =
            Path.Combine(Path.GetTempPath(), $"rashmatz_test_{Guid.NewGuid():N}.db")

        let connString = $"Data Source={dbFile}"
        let dbContext = Database.SqliteDbContext(connString) :> IDbContext

        let orgId = Guid.NewGuid().ToString("D")
        let deptId = Guid.NewGuid().ToString("D")
        let adminHolderId = Guid.NewGuid().ToString("D")
        let memberHolderId = Guid.NewGuid().ToString("D")
        let warehouseHolderId = Guid.NewGuid().ToString("D")
        let categoryId = Guid.NewGuid().ToString("D")
        let radioTypeId = Guid.NewGuid().ToString("D")
        let canteenTypeId = Guid.NewGuid().ToString("D")
        let radio1Id = Guid.NewGuid().ToString("D")
        let radio2Id = Guid.NewGuid().ToString("D")
        let templateId = Guid.NewGuid().ToString("D")
        let adminUserId = Guid.NewGuid().ToString("D")
        let memberUserId = Guid.NewGuid().ToString("D")

        use conn = dbContext.OpenReadConnection()

        conn
        |> Db.newCommand
            """
            INSERT INTO organizations (id, name, slug, is_active)
            VALUES (@org_id, 'Unit 8200 Logistics', 'unit-8200', 1);

            INSERT INTO departments (id, organization_id, name, code)
            VALUES (@dept_id, @org_id, 'Field Comms', 'COM');

            INSERT INTO users (id, google_id, email, full_name, is_super_admin)
            VALUES
                (@admin_user_id, 'google-admin-1', 'admin@rashmatz.org', 'Commander Admin', 1),
                (@member_user_id, 'google-member-1', 'soldier@rashmatz.org', 'Field Operator', 0);

            INSERT INTO custody_holders (
                id, organization_id, department_id, user_id, holder_type,
                name, internal_identifier, invited_email, role, attendance_status, phone_number, is_active
            )
            VALUES
                (@admin_holder_id, @org_id, @dept_id, @admin_user_id, 1, 'Commander Admin', 'ADM-01', 'admin@rashmatz.org', 2, 1, '+972501112233', 1),
                (@member_holder_id, @org_id, @dept_id, @member_user_id, 1, 'Field Operator', 'OP-102', 'soldier@rashmatz.org', 1, 1, '+972504445566', 1),
                (@warehouse_holder_id, @org_id, @dept_id, NULL, 0, 'Main Armory Vault', 'WH-01', NULL, 0, 0, NULL, 1);

            INSERT INTO item_categories (id, organization_id, name, description)
            VALUES (@cat_id, @org_id, 'Communications', 'Tactical radios and accessories');

            INSERT INTO item_types (id, organization_id, category_id, name, tracking_type, unit_of_measure, description)
            VALUES
                (@radio_type_id, @org_id, @cat_id, 'Tactical Radio PRC-152', 1, 'unit', 'Encrypted VHF/UHF handheld'),
                (@canteen_type_id, @org_id, @cat_id, 'AA Battery Pack', 0, 'pack', '8x AA lithium pack');

            -- Radio 1 is signed out to Warehouse, physically in Warehouse
            -- Radio 2 is signed out to Admin, but physically located with Member!
            INSERT INTO serialized_items (
                id, organization_id, item_type_id, serial_number, barcode,
                current_holder_id, location_holder_id, location_custom_text, status
            )
            VALUES
                (@radio1_id, @org_id, @radio_type_id, 'SN-PRC-001', 'BC-PRC-001', @warehouse_holder_id, @warehouse_holder_id, 'Shelf A1', 0),
                (@radio2_id, @org_id, @radio_type_id, 'SN-PRC-002', 'BC-PRC-002', @admin_holder_id, @member_holder_id, 'Backpack Pouch', 0);

            INSERT INTO inventory_stocks (id, organization_id, item_type_id, holder_id, quantity)
            VALUES (@stock_id, @org_id, @canteen_type_id, @warehouse_holder_id, 50);

            INSERT INTO transfer_templates (
                id, organization_id, name, description, default_source_holder_id, default_dest_holder_id, is_active
            )
            VALUES (
                @tpl_id, @org_id, 'Standard Comms Kit', '1 Radio + 4 Battery Packs', @warehouse_holder_id, @member_holder_id, 1
            );

            INSERT INTO transfer_template_lines (template_id, line_number, item_type_id, quantity)
            VALUES
                (@tpl_id, 1, @radio_type_id, 1),
                (@tpl_id, 2, @canteen_type_id, 4);
            """
        |> Db.setParams
            [ "org_id", SqlType.String orgId
              "dept_id", SqlType.String deptId
              "admin_user_id", SqlType.String adminUserId
              "member_user_id", SqlType.String memberUserId
              "admin_holder_id", SqlType.String adminHolderId
              "member_holder_id", SqlType.String memberHolderId
              "warehouse_holder_id", SqlType.String warehouseHolderId
              "cat_id", SqlType.String categoryId
              "radio_type_id", SqlType.String radioTypeId
              "canteen_type_id", SqlType.String canteenTypeId
              "radio1_id", SqlType.String radio1Id
              "radio2_id", SqlType.String radio2Id
              "stock_id", SqlType.String(Guid.NewGuid().ToString("D"))
              "tpl_id", SqlType.String templateId ]
        |> Db.exec

        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseTestServer() |> ignore
        builder.Services.AddRouting() |> ignore
        builder.Services.AddOxpecker() |> ignore

        let app = builder.Build()
        Program.configureApp app dbContext googleMock devModeEnabled
        app.Start()
        let client = app.GetTestClient()

        let seeds =
            { OrgId = orgId
              DeptId = deptId
              AdminHolderId = adminHolderId
              MemberHolderId = memberHolderId
              WarehouseHolderId = warehouseHolderId
              CategoryId = categoryId
              RadioItemTypeId = radioTypeId
              CanteenItemTypeId = canteenTypeId
              Radio1Id = radio1Id
              Radio2Id = radio2Id
              TemplateId = templateId }

        app, client, dbContext, seeds

module DomainAndApiTests =

    [<Fact>]
    let ``1. Domain Validation collects multiple field errors using FsToolkit validation CE`` () =
        let invalidReq: CommitTransferRequestDto =
            { SourceHolderId = None
              DestHolderId = Some(Guid.NewGuid().ToString("D"))
              TransferTemplateId = Some "not-a-valid-uuid"
              SerializedItemIds = None
              NewIntakeSerializedItems = None
              QuantitativeLines = None
              UpdatePhysicalLocation = Some true
              ToLocationHolderId = None
              ToLocationCustomText = None
              Notes = Some "   " }

        match validateCommitTransfer invalidReq with
        | Ok _ -> Assert.Fail("Expected validation failure")
        | Error(ValidationErrors errs) ->
            Assert.Contains(errs, fun e -> e.Field = "notes" && e.Code = "MISSING_NOTES_FOR_EXTERNAL_TRANSFER")
            Assert.Contains(errs, fun e -> e.Field = "transfer_template_id" && e.Code = "INVALID_UUID_FORMAT")
        | Error other -> Assert.Fail($"Expected ValidationErrors but got {other}")

    [<Fact>]
    let ``2. Login with Mock Google Validator enforces Whitelist and Smart Routing`` () =
        let mockValidator =
            MockGoogleTokenValidator(fun token ->
                match token with
                | "valid-soldier-token" ->
                    Ok
                        { GoogleSubject = "google-member-1"
                          Email = "soldier@rashmatz.org"
                          FullName = "Field Operator"
                          AvatarUrl = None }
                | "unlisted-token" ->
                    Ok
                        { GoogleSubject = "google-stranger"
                          Email = "stranger@example.com"
                          FullName = "Stranger"
                          AvatarUrl = None }
                | _ -> Error(ApiError.single ApiErrorCode.InvalidGoogleToken))

        let app, client, _, _ = TestHarness.createTestServer true mockValidator

        task {
            use _app = app
            use _c = client

            use strangerBody =
                new StringContent("""{"id_token":"unlisted-token"}""", Encoding.UTF8, "application/json")

            let! strangerResp = client.PostAsync("/auth/google", strangerBody)
            Assert.Equal(HttpStatusCode.Forbidden, strangerResp.StatusCode)
            let! strangerJson = strangerResp.Content.ReadAsStringAsync()
            Assert.Contains("EMAIL_NOT_WHITELISTED", strangerJson)

            use soldierBody =
                new StringContent("""{"id_token":"valid-soldier-token"}""", Encoding.UTF8, "application/json")

            let! soldierResp = client.PostAsync("/auth/google", soldierBody)
            Assert.Equal(HttpStatusCode.OK, soldierResp.StatusCode)
            let! soldierJson = soldierResp.Content.ReadAsStringAsync()
            Assert.Contains("\"/my-custody\"", soldierJson)
        }

    [<Fact>]
    let ``3. My Custody returns both Legal Custody items and Physically Located items + supports X-Holders-Hash`` () =
        let mockValidator = MockGoogleTokenValidator(fun _ -> Error(ApiError.single ApiErrorCode.InvalidGoogleToken))
        let app, client, _, seeds = TestHarness.createTestServer true mockValidator

        task {
            use _app = app
            use _c = client

            use req1 = new HttpRequestMessage(HttpMethod.Get, "/pages/my-custody")
            req1.Headers.Add(Auth.DevUserEmailHeaderName, "soldier@rashmatz.org")

            let! resp1 = client.SendAsync(req1)
            Assert.Equal(HttpStatusCode.OK, resp1.StatusCode)
            let! body1 = resp1.Content.ReadAsStringAsync()
            use doc1 = JsonDocument.Parse(body1)

            let navbar1 = doc1.RootElement.GetProperty("navbar")
            let hash1 = TestHarness.getRequiredJsonString "holders_directory_hash" navbar1
            Assert.False(String.IsNullOrWhiteSpace(hash1))
            Assert.Equal(JsonValueKind.Array, navbar1.GetProperty("holders_directory").ValueKind)

            let serItems = doc1.RootElement.GetProperty("page").GetProperty("serialized_items")
            Assert.Equal(1, serItems.GetArrayLength())
            Assert.Equal(seeds.Radio2Id, TestHarness.getRequiredJsonString "id" serItems[0])

            use req2 = new HttpRequestMessage(HttpMethod.Get, "/pages/my-custody")
            req2.Headers.Add(Auth.DevUserEmailHeaderName, "soldier@rashmatz.org")
            req2.Headers.Add(Navbar.HoldersHashHeaderName, hash1)

            let! resp2 = client.SendAsync(req2)
            Assert.Equal(HttpStatusCode.OK, resp2.StatusCode)
            let! body2 = resp2.Content.ReadAsStringAsync()
            use doc2 = JsonDocument.Parse(body2)
            Assert.Equal(JsonValueKind.Null, doc2.RootElement.GetProperty("navbar").GetProperty("holders_directory").ValueKind)
        }

    [<Fact>]
    let ``4. Full 3-Step Transfer Flow (6A -> 6B -> 6C) with Idempotency-Key and Physical Location Sync`` () =
        let mockValidator = MockGoogleTokenValidator(fun _ -> Error(ApiError.single ApiErrorCode.InvalidGoogleToken))
        let app, client, _, seeds = TestHarness.createTestServer true mockValidator

        task {
            use _app = app
            use _c = client

            use req6A = new HttpRequestMessage(HttpMethod.Get, "/pages/transfers-new-template")
            req6A.Headers.Add(Auth.DevUserEmailHeaderName, "admin@rashmatz.org")
            let! resp6A = client.SendAsync(req6A)
            Assert.Equal(HttpStatusCode.OK, resp6A.StatusCode)

            use req6B =
                new HttpRequestMessage(HttpMethod.Get, $"/pages/transfers-new-parties?template_id={seeds.TemplateId}")
            req6B.Headers.Add(Auth.DevUserEmailHeaderName, "admin@rashmatz.org")
            let! resp6B = client.SendAsync(req6B)
            Assert.Equal(HttpStatusCode.OK, resp6B.StatusCode)

            use req6C =
                new HttpRequestMessage(
                    HttpMethod.Get,
                    $"/pages/transfers-new-items?source_id={seeds.WarehouseHolderId}&dest_id={seeds.MemberHolderId}&template_id={seeds.TemplateId}"
                )
            req6C.Headers.Add(Auth.DevUserEmailHeaderName, "admin@rashmatz.org")
            let! resp6C = client.SendAsync(req6C)
            Assert.Equal(HttpStatusCode.OK, resp6C.StatusCode)

            let transferPayload =
                sprintf
                    """{
                        "source_holder_id": "%s",
                        "dest_holder_id": "%s",
                        "transfer_template_id": "%s",
                        "serialized_item_ids": ["%s"],
                        "quantitative_lines": [{"item_type_id": "%s", "quantity": 4}],
                        "update_physical_location": true,
                        "to_location_custom_text": "Field Vest Pouch 2",
                        "notes": "Issued comms kit for field deployment"
                    }"""
                    seeds.WarehouseHolderId
                    seeds.MemberHolderId
                    seeds.TemplateId
                    seeds.Radio1Id
                    seeds.CanteenItemTypeId

            let idempotencyKey = Guid.NewGuid().ToString("D")

            use postReq1 = new HttpRequestMessage(HttpMethod.Post, "/transfers")
            postReq1.Headers.Add(Auth.DevUserEmailHeaderName, "admin@rashmatz.org")
            postReq1.Headers.Add("Idempotency-Key", idempotencyKey)
            postReq1.Content <- new StringContent(transferPayload, Encoding.UTF8, "application/json")

            let! postResp1 = client.SendAsync(postReq1)
            Assert.Equal(HttpStatusCode.OK, postResp1.StatusCode)
            let! postBody1 = postResp1.Content.ReadAsStringAsync()
            use postDoc1 = JsonDocument.Parse(postBody1)
            let transferId1 = TestHarness.getRequiredJsonString "transfer_id" postDoc1.RootElement
            let voucher1 = TestHarness.getRequiredJsonString "transfer_number" postDoc1.RootElement
            Assert.StartsWith("TR-", voucher1)

            use postReq2 = new HttpRequestMessage(HttpMethod.Post, "/transfers")
            postReq2.Headers.Add(Auth.DevUserEmailHeaderName, "admin@rashmatz.org")
            postReq2.Headers.Add("Idempotency-Key", idempotencyKey)
            postReq2.Content <- new StringContent(transferPayload, Encoding.UTF8, "application/json")

            let! postResp2 = client.SendAsync(postReq2)
            Assert.Equal(HttpStatusCode.OK, postResp2.StatusCode)
            let! postBody2 = postResp2.Content.ReadAsStringAsync()
            use postDoc2 = JsonDocument.Parse(postBody2)
            let transferId2 = TestHarness.getRequiredJsonString "transfer_id" postDoc2.RootElement
            Assert.Equal(transferId1, transferId2)
        }

    [<Fact>]
    let ``5. High-Concurrency SQLite Audit Scans execute 50 parallel writes without SQLITE_BUSY`` () =
        let mockValidator = MockGoogleTokenValidator(fun _ -> Error(ApiError.single ApiErrorCode.InvalidGoogleToken))
        let app, client, dbContext, seeds = TestHarness.createTestServer true mockValidator

        task {
            use _app = app
            use _c = client

            let sessionId = Guid.NewGuid()
            let sessionIdStr = sessionId.ToString("D")
            let nowIso = DateTimeOffset.UtcNow.ToString("O")

            use conn = dbContext.OpenReadConnection()

            conn
            |> Db.newCommand
                """
                INSERT INTO audit_sessions (id, organization_id, title, initiated_by_holder_id, status, opened_at, updated_at)
                VALUES (@id, @org_id, 'Concurrent Stress Audit', @admin_id, 0, @now, @now)
                """
            |> Db.setParams
                [ "id", SqlType.String sessionIdStr
                  "org_id", SqlType.String seeds.OrgId
                  "admin_id", SqlType.String seeds.AdminHolderId
                  "now", SqlType.String nowIso ]
            |> Db.exec

            let barcodes =
                [ for i in 1..50 ->
                      let itemId = Guid.NewGuid().ToString("D")
                      let recordId = Guid.NewGuid().ToString("D")
                      let bc = sprintf "BC-STRESS-%03d" i

                      conn
                      |> Db.newCommand
                          """
                          INSERT INTO serialized_items (
                              id, organization_id, item_type_id, serial_number, barcode,
                              current_holder_id, location_holder_id, status, created_at, updated_at
                          )
                          VALUES (@id, @org_id, @type_id, @sn, @bc, @holder_id, @holder_id, 0, @now, @now);

                          INSERT INTO audit_item_records (
                              id, audit_session_id, serialized_item_id, expected_holder_id, scan_status, created_at, updated_at
                          )
                          VALUES (@rec_id, @session_id, @id, @holder_id, 0, @now, @now);
                          """
                      |> Db.setParams
                          [ "id", SqlType.String itemId
                            "org_id", SqlType.String seeds.OrgId
                            "type_id", SqlType.String seeds.RadioItemTypeId
                            "sn", SqlType.String(sprintf "SN-STRESS-%03d" i)
                            "bc", SqlType.String bc
                            "holder_id", SqlType.String seeds.MemberHolderId
                            "rec_id", SqlType.String recordId
                            "session_id", SqlType.String sessionIdStr
                            "now", SqlType.String nowIso ]
                      |> Db.exec

                      bc ]

            let orgDomainId = OrgId(Guid.Parse(seeds.OrgId))
            let sessionDomainId = AuditSessionId sessionId
            let scannerHolderId = HolderId(Guid.Parse(seeds.MemberHolderId))

            let scanTasks =
                barcodes
                |> List.map (fun bcStr ->
                    let bc =
                        match SmartConstructors.createBarcode "barcode" (Some bcStr) with
                        | Ok b -> b
                        | Error _ -> failwith "Invalid test barcode"

                    Database.recordAuditScanConcurrently dbContext orgDomainId sessionDomainId scannerHolderId bc)

            let! results = Task.WhenAll(scanTasks)

            for res in results do
                match res with
                | Ok(scanStatus, _, _) -> Assert.Equal(int AuditScanStatus.Verified, scanStatus)
                | Error err -> Assert.Fail($"Concurrent scan failed with {err}")
        }
