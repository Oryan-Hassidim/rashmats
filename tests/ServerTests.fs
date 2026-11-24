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
open Microsoft.Extensions.Hosting
open Donald
open Xunit
open Rashmatz
open Rashmatz.Shared
open Rashmatz.Pages.TransfersNewItems.Domain

type MockGoogleValidator(resolver: string -> Result<GoogleUser, ApiError>) =
    interface IGoogleTokenValidator with
        member _.ValidateAsync(idToken) = Task.FromResult(resolver idToken)

module Harness =
    let strProp (name: string) (el: JsonElement) =
        match el.GetProperty(name).GetString() with
        | null -> failwith $"Expected string property '{name}'"
        | s -> s

    let startServer (devMode: bool) (googleMock: IGoogleTokenValidator) =
        let dbFile = Path.Combine(Path.GetTempPath(), $"rashmatz_test_{Guid.NewGuid():N}.db")
        let db = Database.SqliteDbContext($"Data Source={dbFile}") :> IDbContext
        let seed = Database.ensureDevSeed db

        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseTestServer() |> ignore
        App.registerServices builder.Services

        let app = builder.Build()
        App.configure app db googleMock devMode
        app.Start()
        app, app.GetTestClient(), db, seed

module ServerTests =

    [<Fact>]
    let ``1. Domain validation collects multiple field errors together`` () =
        let req: CommitTransferRequest =
            { SourceHolderId = None; DestHolderId = Some(Guid.NewGuid().ToString("D"))
              TransferTemplateId = Some "not-a-uuid"; SerializedItemIds = None
              NewIntakeSerializedItems = None; QuantitativeLines = None
              UpdatePhysicalLocation = Some true; ToLocationHolderId = None
              ToLocationCustomText = None; Notes = Some "   " }

        match validateCommitTransfer req with
        | Error(ValidationErrors errs) ->
            Assert.Contains(errs, fun e -> e.Field = "notes" && e.Code = "MISSING_NOTES_FOR_EXTERNAL_TRANSFER")
            Assert.Contains(errs, fun e -> e.Field = "transfer_template_id" && e.Code = "INVALID_UUID_FORMAT")
        | other -> Assert.Fail($"Expected ValidationErrors, got {other}")

    [<Fact>]
    let ``2. Login with Mock Google Validator enforces whitelist and smart routing`` () =
        let mock = MockGoogleValidator(function
            | "valid-soldier" -> Ok { Subject = "google-member-1"; Email = "soldier@rashmatz.org"; FullName = "Field Operator"; AvatarUrl = None }
            | "unlisted" -> Ok { Subject = "google-stranger"; Email = "stranger@example.com"; FullName = "Stranger"; AvatarUrl = None }
            | _ -> Error(ApiError.single ApiErrorCode.InvalidGoogleToken))

        let app, client, _, _ = Harness.startServer true mock
        task {
            use _app = app
            use _c = client

            use badBody = new StringContent("""{"id_token":"unlisted"}""", Encoding.UTF8, "application/json")
            let! badResp = client.PostAsync("/auth/google", badBody)
            Assert.Equal(HttpStatusCode.Forbidden, badResp.StatusCode)
            Assert.Contains("EMAIL_NOT_WHITELISTED", (badResp.Content.ReadAsStringAsync().Result))

            use okBody = new StringContent("""{"id_token":"valid-soldier"}""", Encoding.UTF8, "application/json")
            let! okResp = client.PostAsync("/auth/google", okBody)
            Assert.Equal(HttpStatusCode.OK, okResp.StatusCode)
            let! okJson = okResp.Content.ReadAsStringAsync()
            Assert.Contains("\"/my-custody\"", okJson)
        }

    [<Fact>]
    let ``3. My Custody returns physically located items and respects X-Holders-Hash`` () =
        let mock = MockGoogleValidator(fun _ -> Error(ApiError.single ApiErrorCode.InvalidGoogleToken))
        let app, client, _, _ = Harness.startServer true mock
        task {
            use _app = app
            use _c = client

            use req1 = new HttpRequestMessage(HttpMethod.Get, "/pages/my-custody")
            req1.Headers.Add(Auth.DevEmailHeader, "soldier@rashmatz.org")
            let! resp1 = client.SendAsync(req1)
            Assert.Equal(HttpStatusCode.OK, resp1.StatusCode)
            use doc1 = JsonDocument.Parse(resp1.Content.ReadAsStringAsync().Result)

            let hash = Harness.strProp "holders_directory_hash" (doc1.RootElement.GetProperty("navbar"))
            Assert.Equal(JsonValueKind.Array, doc1.RootElement.GetProperty("navbar").GetProperty("holders_directory").ValueKind)
            Assert.Equal(1, doc1.RootElement.GetProperty("page").GetProperty("serialized_items").GetArrayLength())

            use req2 = new HttpRequestMessage(HttpMethod.Get, "/pages/my-custody")
            req2.Headers.Add(Auth.DevEmailHeader, "soldier@rashmatz.org")
            req2.Headers.Add(Navbar.HoldersHashHeader, hash)
            let! resp2 = client.SendAsync(req2)
            Assert.Equal(HttpStatusCode.OK, resp2.StatusCode)
            use doc2 = JsonDocument.Parse(resp2.Content.ReadAsStringAsync().Result)
            Assert.Equal(JsonValueKind.Null, doc2.RootElement.GetProperty("navbar").GetProperty("holders_directory").ValueKind)
        }

    [<Fact>]
    let ``4. Full 3-step transfer flow (6A -> 6B -> 6C) with Idempotency-Key`` () =
        let mock = MockGoogleValidator(fun _ -> Error(ApiError.single ApiErrorCode.InvalidGoogleToken))
        let app, client, _, seed = Harness.startServer true mock
        task {
            use _app = app
            use _c = client

            let getAsAdmin (url: string) =
                let req = new HttpRequestMessage(HttpMethod.Get, url)
                req.Headers.Add(Auth.DevEmailHeader, seed.AdminEmail)
                client.SendAsync(req)

            let! r6a = getAsAdmin "/pages/transfers-new-template"
            Assert.Equal(HttpStatusCode.OK, r6a.StatusCode)
            let! r6b = getAsAdmin $"/pages/transfers-new-parties?template_id={seed.TemplateId}"
            Assert.Equal(HttpStatusCode.OK, r6b.StatusCode)
            let! r6c = getAsAdmin $"/pages/transfers-new-items?source_id={seed.WarehouseHolderId}&dest_id={seed.MemberHolderId}&template_id={seed.TemplateId}"
            Assert.Equal(HttpStatusCode.OK, r6c.StatusCode)

            let payload =
                sprintf """{"source_holder_id":"%s","dest_holder_id":"%s","transfer_template_id":"%s","serialized_item_ids":["%s"],"quantitative_lines":[{"item_type_id":"%s","quantity":4}],"update_physical_location":true,"to_location_custom_text":"Vest Pouch 2","notes":"Field kit"}"""
                    seed.WarehouseHolderId seed.MemberHolderId seed.TemplateId seed.Radio1Id seed.BatteryTypeId

            let key = Guid.NewGuid().ToString("D")
            let postTransfer () =
                let req = new HttpRequestMessage(HttpMethod.Post, "/transfers")
                req.Headers.Add(Auth.DevEmailHeader, seed.AdminEmail)
                req.Headers.Add("Idempotency-Key", key)
                req.Content <- new StringContent(payload, Encoding.UTF8, "application/json")
                client.SendAsync(req)

            let! p1 = postTransfer ()
            Assert.Equal(HttpStatusCode.OK, p1.StatusCode)
            use d1 = JsonDocument.Parse(p1.Content.ReadAsStringAsync().Result)
            let tid1 = Harness.strProp "transfer_id" d1.RootElement

            let! p2 = postTransfer ()
            Assert.Equal(HttpStatusCode.OK, p2.StatusCode)
            use d2 = JsonDocument.Parse(p2.Content.ReadAsStringAsync().Result)
            Assert.Equal(tid1, Harness.strProp "transfer_id" d2.RootElement)
        }

    [<Fact>]
    let ``5. High-concurrency SQLite audit scans execute 50 parallel writes without SQLITE_BUSY`` () =
        let mock = MockGoogleValidator(fun _ -> Error(ApiError.single ApiErrorCode.InvalidGoogleToken))
        let app, client, db, seed = Harness.startServer true mock
        task {
            use _app = app
            use _c = client

            let sid = Guid.NewGuid()
            let now = DateTimeOffset.UtcNow.ToString("O")
            use conn = db.OpenRead()
            conn |> Db.newCommand "INSERT INTO audit_sessions (id, organization_id, title, initiated_by_holder_id, status, opened_at, updated_at) VALUES (@id, @org, 'Stress Audit', @adm, 0, @now, @now)"
            |> Db.setParams [ "id", SqlType.String(sid.ToString("D")); "org", SqlType.String seed.OrganizationId; "adm", SqlType.String seed.AdminHolderId; "now", SqlType.String now ] |> Db.exec

            let barcodes =
                [ for i in 1..50 ->
                    let itemId, recId, bc = Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), sprintf "BC-STRESS-%03d" i
                    conn |> Db.newCommand "INSERT INTO serialized_items (id, organization_id, item_type_id, serial_number, barcode, current_holder_id, location_holder_id, status, created_at, updated_at) VALUES (@id, @org, '66666666-6666-4666-8666-666666666661', @sn, @bc, @hid, @hid, 0, @now, @now); INSERT INTO audit_item_records (id, audit_session_id, serialized_item_id, expected_holder_id, scan_status, created_at, updated_at) VALUES (@rid, @sid, @id, @hid, 0, @now, @now);"
                    |> Db.setParams [ "id", SqlType.String itemId; "org", SqlType.String seed.OrganizationId; "sn", SqlType.String $"SN-STRESS-{i:D3}"; "bc", SqlType.String bc; "hid", SqlType.String seed.MemberHolderId; "rid", SqlType.String recId; "sid", SqlType.String(sid.ToString("D")); "now", SqlType.String now ] |> Db.exec
                    bc ]

            let orgId = OrgId(Guid.Parse seed.OrganizationId)
            let scannerId = HolderId(Guid.Parse seed.MemberHolderId)
            let! results =
                barcodes
                |> List.map (fun bc ->
                    let parsed = match Parse.barcode "bc" (Some bc) with Ok b -> b | Error _ -> failwith "bad"
                    Database.recordAuditScan db orgId (AuditSessionId sid) scannerId parsed)
                |> Task.WhenAll

            for r in results do
                match r with
                | Ok(status, _, _) -> Assert.Equal(int AuditScanStatus.Verified, status)
                | Error e -> Assert.Fail($"Scan failed: {e}")
        }

    [<Fact>]
    let ``6. OpenAPI endpoint serves valid OpenAPI JSON document for Schemathesis`` () =
        let mock = MockGoogleValidator(fun _ -> Error(ApiError.single ApiErrorCode.InvalidGoogleToken))
        let app, client, _, _ = Harness.startServer true mock
        task {
            use _app = app
            use _c = client

            let! resp = client.GetAsync("/openapi/v1.json")
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
            let! body = resp.Content.ReadAsStringAsync()
            use doc = JsonDocument.Parse(body)
            Assert.True(doc.RootElement.TryGetProperty("openapi", ref (Unchecked.defaultof<JsonElement>)))
            Assert.Contains("/pages/my-custody", body)
            Assert.Contains("/transfers", body)
        }
