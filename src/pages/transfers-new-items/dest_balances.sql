SELECT
    it.id AS item_type_id,
    it.tracking_type,
    CASE
        WHEN it.tracking_type = 1 THEN (
            SELECT COUNT(*)
            FROM serialized_items si
            WHERE si.organization_id = @org_id
              AND si.item_type_id = it.id
              AND si.current_holder_id = @holder_id
        )
        ELSE COALESCE((
            SELECT st.quantity
            FROM inventory_stocks st
            WHERE st.organization_id = @org_id
              AND st.item_type_id = it.id
              AND st.holder_id = @holder_id
        ), 0)
    END AS current_quantity
FROM item_types it
WHERE it.organization_id = @org_id;
