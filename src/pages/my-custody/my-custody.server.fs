namespace Rashmatz.Pages.MyCustody

open System.Text.Json.Serialization
open System.Threading.Tasks
open Oxpecker
open Donald
open FsToolkit.ErrorHandling
open Rashmatz.Shared
open Rashmatz.Shared.Schema

/// Vertical Slice for Page 2: My Custody & Physical Possession (`/my-custody`)
/// Co-located inside `src/pages/my-custody/my-custody.server.fs`.
/// Returns both:
/// 1. Equipment signed out in the user's custody (`current_holder_id = me`)
/// 2. Equipment physically located with the user (`location_holder_id = me`) even when signed out to someone else!
module Server =

    type CustodianHeaderDto =
        { [<JsonPropertyName("holder_id")>]
          HolderId: string
          [<JsonPropertyName("name")>]
          Name: string
          [<JsonPropertyName("department_id")>]
          DepartmentId: string option
          [<JsonPropertyName("department_name")>]
          DepartmentName: string option
          [<JsonPropertyName("internal_identifier")>]
          InternalIdentifier: string option
          [<JsonPropertyName("attendance_status")>]
          AttendanceStatus: int }

    type CategorySummaryDto =
        { [<JsonPropertyName("id")>]
          Id: string
          [<JsonPropertyName("name")>]
          Name: string
          [<JsonPropertyName("description")>]
          Description: string option }

    type MyCustodySerializedItemDto =
        { [<JsonPropertyName("id")>]
          Id: string
          [<JsonPropertyName("item_type_id")>]
          ItemTypeId: string
          [<JsonPropertyName("item_type_name")>]
          ItemTypeName: string
          [<JsonPropertyName("category_id")>]
          CategoryId: string option
          [<JsonPropertyName("unit_of_measure")>]
          UnitOfMeasure: string
          [<JsonPropertyName("serial_number")>]
          SerialNumber: string option
          [<JsonPropertyName("barcode")>]
          Barcode: string
          [<JsonPropertyName("status")>]
          Status: int
          [<JsonPropertyName("current_holder_id")>]
          CurrentHolderId: string option
          [<JsonPropertyName("location_holder_id")>]
          LocationHolderId: string option
          [<JsonPropertyName("location_custom_text")>]
          LocationCustomText: string option }

    type MyCustodyQuantitativeItemDto =
        { [<JsonPropertyName("stock_id")>]
          StockId: string
          [<JsonPropertyName("item_type_id")>]
          ItemTypeId: string
          [<JsonPropertyName("item_type_name")>]
          ItemTypeName: string
          [<JsonPropertyName("category_id")>]
          CategoryId: string option
          [<JsonPropertyName("unit_of_measure")>]
          UnitOfMeasure: string
          [<JsonPropertyName("holder_id")>]
          HolderId: string
          [<JsonPropertyName("quantity")>]
          Quantity: int }

    type MyCustodyPageDto =
        { [<JsonPropertyName("custodian")>]
          Custodian: CustodianHeaderDto
          [<JsonPropertyName("categories")>]
          Categories: CategorySummaryDto list
          [<JsonPropertyName("serialized_items")>]
          SerializedItems: MyCustodySerializedItemDto list
          [<JsonPropertyName("quantitative_items")>]
          QuantitativeItems: MyCustodyQuantitativeItemDto list }

    let loadMyCustodyPageAsync
        (db: IDbContext)
        (auth: AuthenticatedContext)
        : Task<MyCustodyPageDto> =
        task {
            let orgIdStr = auth.ActiveOrg.OrganizationId.ToDbString()
            let holderIdStr = auth.ActiveOrg.HolderId.ToDbString()

            use conn = db.OpenReadConnection()

            // 1. Load categories using generated schema reader
            let categoryRows =
                conn
                |> Db.newCommand
                    """
                    SELECT id, organization_id, name, description, created_at, updated_at
                    FROM item_categories
                    WHERE organization_id = @org_id
                    ORDER BY name ASC
                    """
                |> Db.setParams [ "org_id", SqlType.String orgIdStr ]
                |> Db.query main.Readers.read_item_categories

            let categories =
                categoryRows
                |> List.map (fun (c: main.item_categories) ->
                    { Id = c.id
                      Name = c.name
                      Description = c.description })

            // 2. Load custodian department name
            let deptNameOpt =
                match auth.ActiveOrg.DepartmentId with
                | None -> None
                | Some deptId ->
                    conn
                    |> Db.newCommand "SELECT name FROM departments WHERE id = @id AND organization_id = @org_id"
                    |> Db.setParams
                        [ "id", SqlType.String deptId
                          "org_id", SqlType.String orgIdStr ]
                    |> Db.querySingle (fun rd -> rd.ReadString "name")

            // 3. Load serialized items where user is either the legal custodian OR the physical location holder
            let serializedItems =
                conn
                |> Db.newCommand
                    """
                    SELECT
                        si.id,
                        si.item_type_id,
                        it.name AS item_type_name,
                        it.category_id,
                        it.unit_of_measure,
                        si.serial_number,
                        si.barcode,
                        si.status,
                        si.current_holder_id,
                        si.location_holder_id,
                        si.location_custom_text
                    FROM serialized_items si
                    INNER JOIN item_types it ON it.id = si.item_type_id
                    WHERE si.organization_id = @org_id
                      AND (si.current_holder_id = @holder_id OR si.location_holder_id = @holder_id)
                    ORDER BY it.name ASC, si.barcode ASC
                    """
                |> Db.setParams
                    [ "org_id", SqlType.String orgIdStr
                      "holder_id", SqlType.String holderIdStr ]
                |> Db.query (fun rd ->
                    { Id = rd.ReadString "id"
                      ItemTypeId = rd.ReadString "item_type_id"
                      ItemTypeName = rd.ReadString "item_type_name"
                      CategoryId = rd.ReadStringOption "category_id"
                      UnitOfMeasure = rd.ReadString "unit_of_measure"
                      SerialNumber = rd.ReadStringOption "serial_number"
                      Barcode = rd.ReadString "barcode"
                      Status = rd.ReadInt32 "status"
                      CurrentHolderId = rd.ReadStringOption "current_holder_id"
                      LocationHolderId = rd.ReadStringOption "location_holder_id"
                      LocationCustomText = rd.ReadStringOption "location_custom_text" })

            // 4. Load quantitative items allocated to this holder
            let quantitativeItems =
                conn
                |> Db.newCommand
                    """
                    SELECT
                        st.id AS stock_id,
                        st.item_type_id,
                        it.name AS item_type_name,
                        it.category_id,
                        it.unit_of_measure,
                        st.holder_id,
                        st.quantity
                    FROM inventory_stocks st
                    INNER JOIN item_types it ON it.id = st.item_type_id
                    WHERE st.organization_id = @org_id
                      AND st.holder_id = @holder_id
                      AND st.quantity > 0
                    ORDER BY it.name ASC
                    """
                |> Db.setParams
                    [ "org_id", SqlType.String orgIdStr
                      "holder_id", SqlType.String holderIdStr ]
                |> Db.query (fun rd ->
                    { StockId = rd.ReadString "stock_id"
                      ItemTypeId = rd.ReadString "item_type_id"
                      ItemTypeName = rd.ReadString "item_type_name"
                      CategoryId = rd.ReadStringOption "category_id"
                      UnitOfMeasure = rd.ReadString "unit_of_measure"
                      HolderId = rd.ReadString "holder_id"
                      Quantity = rd.ReadInt32 "quantity" })

            return
                { Custodian =
                    { HolderId = holderIdStr
                      Name = auth.ActiveOrg.HolderName
                      DepartmentId = auth.ActiveOrg.DepartmentId
                      DepartmentName = deptNameOpt
                      InternalIdentifier = auth.ActiveOrg.InternalIdentifier
                      AttendanceStatus = int auth.ActiveOrg.AttendanceStatus }
                  Categories = categories
                  SerializedItems = serializedItems
                  QuantitativeItems = quantitativeItems }
        }

    let getMyCustodyBootstrapHandler (db: IDbContext) (devMode: bool) : EndpointHandler =
        Http.runWorkflow (fun ctx ->
            taskResult {
                let! auth = Auth.requireMember db devMode ctx
                let! navbar = Navbar.buildNavbarAsync db ctx auth
                let! page = loadMyCustodyPageAsync db auth

                return
                    ({ Navbar = navbar
                       Page = page }
                    : Navbar.BootstrapEnvelopeDto<MyCustodyPageDto>)
            })

    let endpoints (db: IDbContext) (devMode: bool) : Endpoint list =
        [ GET [ route "/pages/my-custody" (getMyCustodyBootstrapHandler db devMode) ] ]
