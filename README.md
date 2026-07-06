# Rashmatz (Equipment & Personnel List)
A multi-tenant system for managing, tracking, and verifying inventory and personnel, tailored for hybrid office and field operations.

---

## Core Entities

*   **Tenants (Organizations):** Complete logical separation of all data between different organizations in the system.
*   **People:** Members of the organization associated with their respective umbrella entity.
*   **Equipment:**
    *   **Quantitative Equipment:** Items managed by count and stock levels (e.g., shirts).
    *   **Serialized Equipment:** Unique items that hold a serial number and barcode (e.g., binoculars).

---

## User Roles

*   **Administrators:** Manage lists of people and equipment, record equipment transfers between individuals, and initiate inventory audits.
*   **End Users:** View the status of the personal equipment assigned to them, and perform barcode scans to verify the inventory in their possession during an active audit.

---

## Core Process: Field Inventory Audit

1.  **Initiation:** An administrator triggers an inventory audit (Default: organization-wide, with options for future segmentation).
2.  **Distribution:** The system sends a cross-organizational notification to relevant users via a dedicated Telegram bot or web push notifications.
3.  **Verification:** End users log into the system from their mobile devices, physically scan the barcodes of the serialized equipment in their possession, and confirm attendance/status.
4.  **Control:** The system displays completion percentages for the organization's audit in real-time.

---

## Access & Authentication

*   Logins are strictly handled via **Google Account Authentication** only.
*   Access is granted **exclusively to pre-approved users** who have been added to the system by an administrator (Whitelist).
*   The system maintains a continuous user session as long as the Google authentication remains valid.

---

## Roadmap

*   Roster and shift management for organization personnel.
*   Attendance tracking and leave management.
*   Expanding the Telegram bot to support two-way interaction (asking questions and performing actions directly from the chat).

