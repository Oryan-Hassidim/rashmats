// Strongly-typed immutable schema records and data readers for all SQLite tables defined in `db model.md`.
// Compatible with `dotnet sqlhydra sqlite` (Standalone / Donald mode) with zero external ORM runtime dependencies.
namespace Rashmatz.Shared.Schema

open System.Data
open Donald

module main =

    [<CLIMutable>]
    type users =
        { id: string
          google_id: string
          email: string
          full_name: string
          avatar_url: Option<string>
          is_super_admin: int
          created_at: string
          updated_at: string }

    [<CLIMutable>]
    type organizations =
        { id: string
          name: string
          slug: string
          is_active: int
          created_at: string
          updated_at: string }

    [<CLIMutable>]
    type departments =
        { id: string
          organization_id: string
          name: string
          code: Option<string>
          created_at: string
          updated_at: string }

    [<CLIMutable>]
    type custody_holders =
        { id: string
          organization_id: string
          department_id: Option<string>
          user_id: Option<string>
          holder_type: int
          name: string
          internal_identifier: Option<string>
          invited_email: Option<string>
          role: int
          attendance_status: int
          phone_number: Option<string>
          is_active: int
          created_at: string
          updated_at: string }

    [<CLIMutable>]
    type certifications =
        { id: string
          organization_id: string
          name: string
          description: Option<string>
          validity_months: Option<int>
          created_at: string
          updated_at: string }

    [<CLIMutable>]
    type holder_certifications =
        { id: string
          holder_id: string
          certification_id: string
          acquired_at: string
          expires_at: Option<string>
          certificate_number: Option<string>
          notes: Option<string>
          updated_at: string }

    [<CLIMutable>]
    type item_categories =
        { id: string
          organization_id: string
          name: string
          description: Option<string>
          created_at: string
          updated_at: string }

    [<CLIMutable>]
    type item_types =
        { id: string
          organization_id: string
          category_id: Option<string>
          name: string
          tracking_type: int
          unit_of_measure: string
          description: Option<string>
          created_at: string
          updated_at: string }

    [<CLIMutable>]
    type serialized_items =
        { id: string
          organization_id: string
          item_type_id: string
          serial_number: Option<string>
          barcode: string
          current_holder_id: Option<string>
          location_holder_id: Option<string>
          location_custom_text: Option<string>
          status: int
          created_at: string
          updated_at: string }

    [<CLIMutable>]
    type inventory_stocks =
        { id: string
          organization_id: string
          item_type_id: string
          holder_id: string
          quantity: int
          updated_at: string }

    [<CLIMutable>]
    type transfer_templates =
        { id: string
          organization_id: string
          name: string
          description: Option<string>
          default_source_holder_id: Option<string>
          default_dest_holder_id: Option<string>
          is_active: int
          created_at: string
          updated_at: string }

    [<CLIMutable>]
    type transfer_template_lines =
        { template_id: string
          line_number: int
          item_type_id: string
          quantity: int }

    [<CLIMutable>]
    type custody_transfers =
        { id: string
          organization_id: string
          transfer_template_id: Option<string>
          transfer_number: string
          admin_holder_id: string
          source_holder_id: Option<string>
          dest_holder_id: Option<string>
          notes: Option<string>
          transferred_at: string }

    [<CLIMutable>]
    type custody_transfer_lines =
        { transfer_id: string
          line_number: int
          serialized_item_id: Option<string>
          item_type_id: Option<string>
          quantity: int }

    [<CLIMutable>]
    type serialized_item_location_logs =
        { id: string
          organization_id: string
          serialized_item_id: string
          admin_holder_id: string
          from_location_holder_id: Option<string>
          from_location_custom_text: Option<string>
          to_location_holder_id: Option<string>
          to_location_custom_text: Option<string>
          notes: Option<string>
          recorded_at: string }

    [<CLIMutable>]
    type audit_sessions =
        { id: string
          organization_id: string
          title: string
          initiated_by_holder_id: string
          status: int
          opened_at: string
          closed_at: Option<string>
          updated_at: string }

    [<CLIMutable>]
    type audit_item_records =
        { id: string
          audit_session_id: string
          serialized_item_id: string
          expected_holder_id: Option<string>
          scan_status: int
          scanned_by_holder_id: Option<string>
          scanned_at: Option<string>
          resolved_by_admin_id: Option<string>
          resolution_notes: Option<string>
          created_at: string
          updated_at: string }

    /// Strongly-typed Hydra/Donald readers for mapping SQLite rows directly to immutable table records.
    module Readers =
        let read_item_categories (rd: IDataReader) : item_categories =
            { id = rd.ReadString "id"
              organization_id = rd.ReadString "organization_id"
              name = rd.ReadString "name"
              description = rd.ReadStringOption "description"
              created_at = rd.ReadString "created_at"
              updated_at = rd.ReadString "updated_at" }

        let read_transfer_templates (rd: IDataReader) : transfer_templates =
            { id = rd.ReadString "id"
              organization_id = rd.ReadString "organization_id"
              name = rd.ReadString "name"
              description = rd.ReadStringOption "description"
              default_source_holder_id = rd.ReadStringOption "default_source_holder_id"
              default_dest_holder_id = rd.ReadStringOption "default_dest_holder_id"
              is_active = rd.ReadInt32 "is_active"
              created_at = rd.ReadString "created_at"
              updated_at = rd.ReadString "updated_at" }

        let read_audit_sessions (rd: IDataReader) : audit_sessions =
            { id = rd.ReadString "id"
              organization_id = rd.ReadString "organization_id"
              title = rd.ReadString "title"
              initiated_by_holder_id = rd.ReadString "initiated_by_holder_id"
              status = rd.ReadInt32 "status"
              opened_at = rd.ReadString "opened_at"
              closed_at = rd.ReadStringOption "closed_at"
              updated_at = rd.ReadString "updated_at" }
