namespace Rashmatz.Pages.MyCustody

open Oxpecker
open Oxpecker.OpenApi
open Donald
open FsToolkit.ErrorHandling
open Rashmatz.Shared

/// Vertical Slice — Page 2: My Custody & Physical Possession (`/my-custody`)
module Server =

    type CustodianHeader =
        { HolderId: string; Name: string; DepartmentId: string option
          DepartmentName: string option; InternalIdentifier: string option; AttendanceStatus: int }

    type CategorySummary = { Id: string; Name: string; Description: string option }

    type CustodySerializedItem =
        { Id: string; ItemTypeId: string; ItemTypeName: string; CategoryId: string option
          UnitOfMeasure: string; SerialNumber: string option; Barcode: string; Status: int
          CurrentHolderId: string option; LocationHolderId: string option; LocationCustomText: string option }

    type CustodyQuantitativeItem =
        { StockId: string; ItemTypeId: string; ItemTypeName: string; CategoryId: string option
          UnitOfMeasure: string; HolderId: string; Quantity: int }

    type MyCustodyPage =
        { Custodian: CustodianHeader
          Categories: CategorySummary list
          SerializedItems: CustodySerializedItem list
          QuantitativeItems: CustodyQuantitativeItem list }

    let private loadPage (db: IDbContext) (auth: AuthContext) : MyCustodyPage =
        use conn = db.OpenRead()
        let orgId, holderId = auth.ActiveOrg.OrgId.Str, auth.ActiveOrg.HolderId.Str

        let categories =
            conn
            |> Db.newCommand "SELECT id, name, description FROM item_categories WHERE organization_id=@org ORDER BY name"
            |> Db.setParams [ "org", SqlType.String orgId ]
            |> Db.query (fun rd -> { Id = rd.ReadString "id"; Name = rd.ReadString "name"; Description = rd.ReadStringOption "description" })

        let deptName =
            auth.ActiveOrg.DepartmentId
            |> Option.bind (fun did ->
                conn |> Db.newCommand "SELECT name FROM departments WHERE id=@id AND organization_id=@org"
                |> Db.setParams [ "id", SqlType.String did; "org", SqlType.String orgId ]
                |> Db.querySingle (fun rd -> rd.ReadString "name"))

        let serialized =
            conn
            |> Db.newCommand
                """
                SELECT si.id, si.item_type_id, it.name AS type_name, it.category_id, it.unit_of_measure,
                       si.serial_number, si.barcode, si.status, si.current_holder_id, si.location_holder_id, si.location_custom_text
                FROM serialized_items si
                INNER JOIN item_types it ON it.id = si.item_type_id
                WHERE si.organization_id = @org AND (si.current_holder_id = @hid OR si.location_holder_id = @hid)
                ORDER BY it.name, si.barcode
                """
            |> Db.setParams [ "org", SqlType.String orgId; "hid", SqlType.String holderId ]
            |> Db.query (fun rd ->
                { Id = rd.ReadString "id"; ItemTypeId = rd.ReadString "item_type_id"; ItemTypeName = rd.ReadString "type_name"
                  CategoryId = rd.ReadStringOption "category_id"; UnitOfMeasure = rd.ReadString "unit_of_measure"
                  SerialNumber = rd.ReadStringOption "serial_number"; Barcode = rd.ReadString "barcode"; Status = rd.ReadInt32 "status"
                  CurrentHolderId = rd.ReadStringOption "current_holder_id"; LocationHolderId = rd.ReadStringOption "location_holder_id"
                  LocationCustomText = rd.ReadStringOption "location_custom_text" })

        let quantitative =
            conn
            |> Db.newCommand
                """
                SELECT st.id AS stock_id, st.item_type_id, it.name AS type_name, it.category_id, it.unit_of_measure, st.holder_id, st.quantity
                FROM inventory_stocks st
                INNER JOIN item_types it ON it.id = st.item_type_id
                WHERE st.organization_id = @org AND st.holder_id = @hid AND st.quantity > 0
                ORDER BY it.name
                """
            |> Db.setParams [ "org", SqlType.String orgId; "hid", SqlType.String holderId ]
            |> Db.query (fun rd ->
                { StockId = rd.ReadString "stock_id"; ItemTypeId = rd.ReadString "item_type_id"; ItemTypeName = rd.ReadString "type_name"
                  CategoryId = rd.ReadStringOption "category_id"; UnitOfMeasure = rd.ReadString "unit_of_measure"
                  HolderId = rd.ReadString "holder_id"; Quantity = rd.ReadInt32 "quantity" })

        { Custodian =
            { HolderId = holderId; Name = auth.ActiveOrg.HolderName; DepartmentId = auth.ActiveOrg.DepartmentId
              DepartmentName = deptName; InternalIdentifier = auth.ActiveOrg.InternalIdentifier
              AttendanceStatus = int auth.ActiveOrg.AttendanceStatus }
          Categories = categories; SerializedItems = serialized; QuantitativeItems = quantitative }

    let endpoints (db: IDbContext) (devMode: bool) : Endpoint list =
        [ GET [
            route "/pages/my-custody" (Http.runWorkflow (fun ctx ->
                taskResult {
                    let! auth = Auth.requireMember db devMode ctx
                    return ({ Navbar = Navbar.build db ctx auth; Page = loadPage db auth } : Navbar.PageEnvelope<MyCustodyPage>)
                }))
            |> addOpenApiSimple<unit, Navbar.PageEnvelope<MyCustodyPage>>
          ] ]
