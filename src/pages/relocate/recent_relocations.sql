WITH latest_logs AS (
    SELECT
        l.serialized_item_id,
        l.recorded_at,
        l.admin_holder_id,
        l.notes,
        ROW_NUMBER() OVER (PARTITION BY l.serialized_item_id ORDER BY l.recorded_at DESC, l.id DESC) AS rn
    FROM serialized_item_location_logs l
    WHERE l.organization_id = @org_id
)
SELECT
    si.id AS serialized_item_id,
    si.item_type_id,
    it.name AS item_type_name,
    si.barcode,
    si.serial_number,
    si.status,
    si.current_holder_id,
    si.location_holder_id,
    si.location_custom_text,
    ll.recorded_at AS last_relocated_at,
    ll.admin_holder_id AS last_relocated_by_admin_id,
    ll.notes AS last_notes
FROM latest_logs ll
INNER JOIN serialized_items si ON si.id = ll.serialized_item_id
INNER JOIN item_types it ON it.id = si.item_type_id
WHERE ll.rn = 1
ORDER BY ll.recorded_at DESC
LIMIT 20;
