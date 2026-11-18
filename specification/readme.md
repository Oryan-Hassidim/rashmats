# Rashmatz (Inventory & Asset Verification)
A multi-tenant platform for managing, tracking, and verifying equipment and personnel, designed for dynamic operations across office, warehouse, and field environments.

---

## Core Entities

-   **Tenants (Organizations):** Complete logical data isolation per organization.
-   **People:** Registered members belonging to an organization.
-   **Locations / Custody Points:** Static or mobile storage entities that hold inventory (e.g., central warehouse, container, vehicle, supply room).
-   **Equipment:**
    *   **Quantitative Equipment:** Consumables and bulk items tracked by quantity and stock levels (e.g., uniforms, office supplies, protective gear).
    *   **Serialized Equipment:** High-value or sensitive assets tracked individually by unique serial number and barcode (e.g., laptops, radios, heavy tools).

---

## User Roles & Permissions

-   **Administrators:**
    *   Manage lists of people, locations, and equipment.
    *   **Authorized Custody Transfers:** Solely responsible for initiating and logging transfers of equipment between any entities (Person ↔ Person, Person ↔ Location, Location ↔ Location).
    *   Initiate inventory audits and resolve discrepancy alerts.
-   **End Users:**
    *   View their assigned equipment custody in real time.
    *   Perform barcode scans from their mobile devices during audits.

---

## Core Process: Equipment Transfers

-   **Admin-Initiated Only:** Equipment can only be reassigned by an administrator.
-   **Audit Log:** Every transfer creates an immutable record tracking source, destination, timestamp, and the authorizing admin.

---

## Core Process: Field Inventory Audit

1.  **Initiation:** An administrator triggers an active audit (organization-wide or scoped by department/unit).
2.  **Serialized Verification:** End users scan the physical barcodes of the serialized assets currently in their possession. Quantitative items are excluded from manual audit scans.
3.  **Real-Time Discrepancy Detection:**
    *   If a scanned serialized asset is assigned to a different person/location, the system flags an immediate **custody mismatch alert** for administrators.
    *   Real-time progress dashboards track completion percentages and missing assets.

---

## Access & Authentication

-   **Google OAuth:** Secure authentication strictly handled via Google Account login.
-   **Multi-Org Membership:** A single Google identity can belong to multiple tenants/organizations with distinct roles in each.
-   **Admin Whitelist:** Access is restricted to pre-authorized email addresses provisioned by organization administrators.
-   **Persistent Sessions:** Keeps field users logged in for seamless scanning.

---

## Roadmap

-   Shift and duty roster management for organizational personnel.
-   Attendance and availability tracking.
-   Telegram bot and in-app chat integration for natural-language status queries and quick actions.