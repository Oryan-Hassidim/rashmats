# Client Application Specification - Rashmatz

## 1. Architectural Foundation & Global Shell

### 1.1 Architecture & Hosting Paradigm

* **Platform:** Web application structured around discrete pages and clean routes, communicating with a backend API.

* **Client State & Tenancy Resolution:**

  * Active tenant identity (`active_org_id` / `active_org_slug`) is stored on the client side via persistent browser storage or secure session cookies.

  * Clean URL paths (e.g., `/my-custody`, `/transfers/new`, `/inventory`) without specialized routing rewrites.

  * Organization Switcher dropdown in the global navigation bar modifies the stored tenant and triggers a context reload.

* **Responsiveness Paradigm:**

  * **Mobile-First Core:** Member views, Field Transfer Cart, Quick Relocation, and Field Audit Verification. Designed for one-handed touch interaction and integrated device camera usage.

  * **Desktop-First Core:** Executive Dashboard, Equipment Catalog & Groupings, Holders Directory, and Audit Setup Wizard. Optimized for dense tables, complex multi-column filters, and bulk actions.

* **Hardware Integration:**

  * Barcode and QR code scanning utilizes standard device camera feeds. Support is also provided for hardware/laser scanners that deliver barcode values as direct text input.

* **Network Resilience:**

  * Online operation. Network interruptions during scans or form submissions immediately present an explicit, non-blocking error notification with a retry option.

### 1.2 Global Shell & Navigation

#### Unauthenticated State (`/login`)

* **Layout:** Centered card interface.

* **Components:**

  * Branding: Application title and organization identifier.

  * Single Sign-On entry action to initiate identity verification.

  * Status/Feedback container: Handles unauthorized access errors (e.g., "Your email is not whitelisted in any active organization").

#### Authenticated Navigation Bar (Global Header)

* **Role-Based Dynamic Navigation:**

  * Evaluates user role within active tenant:

    * **Member (Standard User):** Links limited to:

      1. My Custody (`/my-custody`)

      2. Transfer History (`/my-transfers`)

      3. Active Audit Scan (`/audit/scan` - conditionally visible if an audit session is open)

    * **Admin (Administrator):** Complete navigation suite:

      1. Dashboard (`/admin/dashboard`)

      2. Transfers Hub (`/transfers/new`, `/transfers/history`)

      3. Inventory & Catalog (`/admin/inventory`)

      4. Custody Holders & People (`/admin/holders`)

      5. Audits Center (`/admin/audits`)

      6. Quick Relocate (`/items/relocate`)

      7. Switch to Personal View (`/my-custody`)

* **Tenant Context Dropdown:**

  * Displays the currently active organization name.

  * Dropdown lists all organizations where the authenticated user possesses an active record.

  * Selection updates client-side tenant state and reloads context.

* **Profile & Account Widget:**

  * User avatar and full name.

  * Current attendance status badge (Read-only for Members; quick-toggle for Admins).

  * Logout action.

## 2. End-User / Member Module (Mobile-First)

### Screen M-01: My Custody (`/my-custody`)

* **Primary Goal:** Enable personnel to review all physical and quantitative equipment under their direct custody and confirm assigned locations.

* **Layout:** Vertical card list with sticky segmented switch (`Serialized` | `Quantitative`).

* **Components & Sections:**

  1. **Custodian Header Badge:**

     * Member name, department, internal identifier (badge #).

     * Attendance badge: `Present`, `Home/Remote`, or `Inactive` (with subtitle: "Managed by Admin").

  2. **Serialized Items Tab:**

     * Dynamic search bar filtering local items by name, model, serial number, or barcode.

     * Item Card:

       * Asset name and category tag.

       * Identifiers: Serial Number and Barcode.

       * Operational Status chip: `Active`, `Maintenance`, `Lost`, or `Decommissioned`.

       * Physical Location Indicator: Displays entity name (e.g., "Vehicle 4", "Main Armory") and/or custom text note (e.g., "Locker 12").

  3. **Quantitative Items Tab:**

     * List of consumable or bulk allocations.

     * Row elements: Item type name, category, allocated quantity count, and unit of measure (e.g., `4 units`, `2 boxes`).

* **Data Attributes & Handlers:**

  * Read-only data feed of assets and stock balances assigned to the authenticated user.

### Screen M-02: Member Transfer History (`/my-transfers`)

* **Primary Goal:** Provide accountability by allowing users to audit every inbound and outbound custody transition concerning their name.

* **Layout:** Chronological timeline view with search and date filters.

* **Components:**

  * Transfer Record Card:

    * Transfer voucher identifier (e.g., `TR-2026-00104`).

    * Direction badge: `Received` (Inbound) or `Handed Over` (Outbound).

    * Counterparty: Origin holder or Destination holder name.

    * Timestamp and Authorizing Admin name.

    * Expandable Drawer: Displays individual lines transferred, distinguishing serialized items and quantitative amounts.

    * Optional Transfer Note (if recorded).

### Screen M-03: Field Audit Verification (`/audit/scan`)

* **Primary Goal:** Enable fast, continuous camera scanning of physical assets during an active organizational audit.

* **Conditional Guard:** Screen is active only if there is an open session. If closed, display "No active audit session in progress".

* **Layout:** High-contrast camera viewport occupying upper 45% of viewport; progress overview and recent scan feedback log in lower 55%.

* **Components & Scan Engine:**

  1. **Continuous Camera Viewport:**

     * Integrated video stream with targeted scanning frame.

     * Flashlight / Torch toggle button.

     * Manual input fallback: Expandable text box to submit barcode manually.

  2. **Personal Audit Meter:**

     * Progress Bar: $\frac{\text{Scanned My Items}}{\text{Total Expected My Items}} \times 100$.

     * Live Counter: "X of Y verified".

  3. **Real-Time Sound & Visual Haptic Feedback:**

     * **Match / Verified (Green):**

       * Trigger: Asset exists in checklist and matches current user's custody.

       * Visual: Green flash border, checkmark icon, item name confirmation.

       * Audio/Vibration: High single pitch tone, single short vibration.

     * **Discrepancy / Mismatch (Amber / Orange):**

       * Trigger: Asset exists in active checklist, but belongs to a different holder.

       * Visual: Orange pulsing border, alert banner: "Asset belongs to \[Expected Holder Name\]. Flagged for Admin review."

       * Audio/Vibration: Double low pitch tone, two vibration pulses.

     * **Invalid / Out of Scope (Red):**

       * Trigger: Scanned barcode does not exist in the active audit session.

       * Visual: Red warning dialogue: "Item is not part of this audit session."

       * Audio/Vibration: Long error buzz.

  4. **Recent Scans Feed:**

     * Reverse-chronological list showing last 5 scanned items in current session with timestamp and result chip.

## 3. Admin Field Operations (Mobile-First / Hybrid)

### Screen A-01: Quick Physical Relocation (`/items/relocate`)

* **Primary Goal:** Rapidly reassign the physical location of a serialized asset without performing a formal custody transfer of legal accountability.

* **Layout:** Single-flow mobile form with camera intake.

* **Workflow:**

  1. **Scan or Lookup Asset:** Camera scanning box or serial/barcode auto-complete search.

  2. **Current State Card:** Shows item title, serial number, current legal custody holder, and current physical location.

  3. **Location Reassignment Inputs (Flexible Location Model):**

     * Requirement: At least one field must be populated (Holder Entity, Custom Text, or both).

     * **Location Holder Selector:** Dropdown or search picker for registered locations (e.g., warehouses, vehicles, storage bins).

     * **Custom Location Text:** Free-text input for granular notes (e.g., "Shelf 4, Box B", "Inside Glovebox").

     * **Notes / Reason:** Optional relocation comment.

  4. **Submit Action:**

     * Validates that at least one location attribute is non-null.

     * Records immutable entry in the location movement log.

     * Updates current item location.

     * Displays success confirmation and immediately resets camera for next item.

### Screen A-02: Transfer Cart & Intake Wizard (`/transfers/new`)

* **Primary Goal:** Admin workflow to execute custody transfers, inbound intakes, external loans, and bulk kit provisioning.

* **Layout:** Multi-step reactive cart view optimized for mobile and desktop.

* **State Machine & Flow:**

```
 [1. Parties Selection] ──> [2. Template Loading (Opt)] ──> [3. Cart Staging] ──> [4. Review & Commit]

```

1. **Parties Selection:**

   * **Transfer Mode Toggle:** `Internal Transfer` | `Intake (Supplier / External)` | `Disposal / External Loan`.

   * **Source Holder:** Selector for Person or Location entity. If *Intake*, locked to empty (External).

   * **Destination Holder:** Selector for Person or Location entity. If *Disposal*, locked to empty (External).

   * **Validation Constraint:** If either Source or Destination is empty, the `Notes` field becomes strictly mandatory.

2. **Transfer Templates Integration:**

   * "Load Template" button: Opens modal with predefined organization equipment templates (e.g., "Standard Field Kit", "Workstation Bundle").

   * Selecting a template automatically pre-populates default item definitions and target quantities into the cart.

   * If template has pre-configured Source/Destination defaults, prompts to apply them.

3. **Cart Assembly (Line Items):**

   * **Add Serialized Asset:**

     * Camera barcode scan or manual input.

     * Validation: System validates item exists and is currently in the custody of the selected source holder (unless mode is *Intake*).

     * Adds line with item reference and quantity of 1.

   * **Add Quantitative Stock:**

     * Item Type search picker.

     * Quantity stepper/input.

     * Validation: Verifies source holder possesses sufficient available stock balance (unless mode is *Intake*).

   * Cart list display allows deletion or quantity changes before committing.

4. **Execution & Confirmation:**

   * Submit button: "Authorize & Commit Transfer".

   * Generates unique voucher number (e.g., `TR-2026-XXXXX`).

   * Inserts transfer record and corresponding transfer lines.

   * Atomic inventory update: Moves serialized items' custody and shifts balances for quantitative stock.

   * Post-action modal: "Transfer Recorded Successfully" with quick actions:

     * "Share Voucher Summary" (generates clipboard text summary suitable for messaging apps).

     * "Start Another Transfer".

### Screen A-03: Active Audit Command Center (`/admin/audits/active`)

* **Primary Goal:** Real-time field supervision and incident resolution during an open audit.

* **Layout:** Mobile tabbed view / Desktop two-column dashboard.

* **Components:**

  1. **Status KPI Bar:**

     * Session title and creation timestamp.

     * Overall progress gauge: Percentage computed from verified and mismatched records over total session records:
       

       $$
       \text{Progress} = \left(\frac{N_{\text{verified}} + N_{\text{mismatch}}}{N_{\text{total}}}\right) \times 100
       $$

     * Counters: Verified count, Discrepancies count, Pending count.

  2. **Discrepancy / Mismatch Live Feed:**

     * Displays items where status is Mismatch.

     * Alert Card details:

       * Item name and barcode.

       * Expected Custodian vs. Actual Scanning Custodian.

       * Scan timestamp and device holder identity.

     * **Action Trigger:** "Resolve Discrepancy" button opens Resolution Dialog (M-03a).

  3. **Resolution Dialog (Modal M-03a):**

     * Option A: **"Reassign Custody (Transfer)"**

       * Prompts to confirm an automated transfer moving item from Expected Holder to Actual Scanner.

       * Requires admin confirmation notes.

       * Records resolution and updates item status.

     * Option B: **"Close Discrepancy with Note"**

       * Keeps custody unchanged (e.g., "Physical hand-over cancelled; item returned to rightful custodian").

       * Input: Mandatory resolution note.

       * Records resolution note and admin identity.

  4. **Session Controls:**

     * "Close Audit Session" button: Finalizes scanning phase, transitions session status to closed.

## 4. Admin Management Modules (Desktop-First)

### Screen A-04: Executive Dashboard (`/admin/dashboard`)

* **Primary Goal:** High-level operational awareness of fleet readiness, attendance, and compliance.

* **Key Widgets:**

  1. **Inventory Readiness Summary:**

     * Total serialized assets by operational status (`Active`, `Maintenance`, `Lost`, `Decommissioned`).

     * Low-stock warnings on monitored quantitative consumables.

  2. **Personnel Attendance Summary:**

     * Live counts: `Present`, `Home/Remote`, `Inactive`.

     * Quick link to Custody Holders directory.

  3. **Active Operations Banner:**

     * Notification if an audit session is currently open.

     * Recent transfer vouchers log (last 10 records).

### Screen A-05: Inventory & Equipment Catalog (`/admin/inventory`)

* **Primary Goal:** Comprehensive tracking, filtering, grouped viewing, and lifecycle inspection of organizational assets.

* **View Toggle Bar:**

  * `Group by Product Type`

  * `Group by Custodian / Location`

  * `Flat Serialized Assets List`

* **Features by View Mode:**

  1. **Group by Product Type:**

     * Expandable hierarchical list: Category $\rightarrow$ Item Type $\rightarrow$ Specific Serialized Assets or Stock Levels.

     * Quantitative overview: Aggregate organization stock across all holders.

  2. **Group by Custodian / Location:**

     * Accordion rows per Holder (Person or Storage Location).

     * Direct display of all held assets and quantities per holder.

  3. **Flat Serialized Assets Table:**

     * Filter Bar: Category, Status, Department, Holder, Location Entity, and text search.

     * Row items: Barcode, Serial Number, Item Type, Current Custody Holder, Physical Location (Entity + Custom Text), Status, Actions.

* **Item Lifecycle Drawer / Timeline View (`/admin/inventory/item/:id`):**

  * Triggered by clicking any serialized item row.

  * Comprehensive metadata: Catalog specifications, serial number, barcode, creation date.

  * **Unified Activity Timeline:**

    * Filterable event stream: All Events | Custody Transfers | Relocations | Audit Scans.

    * Custody transfer nodes: Shows Source, Destination, Authorizing Admin, Voucher ID, and notes.

    * Relocation nodes: Shows From Location, To Location, notes, and admin.

    * Audit nodes: Shows session name, scanner, timestamp, and verification status.

### Screen A-06: Custody Holders & Permissions (`/admin/holders`)

* **Primary Goal:** Manage organizational units, personnel, physical storage locations, attendance, and authorization whitelist.

* **Layout:** Tabbed table view (`Personnel` | `Storage Locations` | `Departments`).

* **Personnel Tab Features:**

  * Table Columns: Name, Internal Identifier, Department, Contact Phone, Whitelisted Email, System Role, Attendance Status, Held Assets Count, Actions.

  * **Inline Attendance Management:** Admin can modify attendance status (`Present`, `Home`, `Inactive`) directly via dropdown on each row.

  * **Quick Inventory Inspection:** Clicking "Held Assets Count" opens an overlay showing all serialized and quantitative items currently signed out to this individual.

  * **Holder Detail Drawer:**

    * Personal metadata and identity account linkage state.

    * Qualifications / Certifications catalog: List of acquired certifications with issue and expiration dates.

  * **Add/Invite Person Modal:** Captures Full Name, Department, Internal ID, Phone, Whitelisted Email, and Role (`Member` or `Admin`).

* **Storage Locations Tab Features:**

  * Table of physical locations, e.g., warehouses, vehicles, containers, cabinets.

  * Flat structure: Displays Name, Internal Identifier, Department, and Active Equipment Stored.

  * Add Location Modal: Captures Name, Identifier, Department. Human-specific attributes (email, attendance, user credentials) are strictly omitted.

### Screen A-07: Audit Session Setup Wizard (`/admin/audits/new`)

* **Primary Goal:** Initialize an immutable, closed-scope audit checklist based on granular organizational criteria.

* **Workflow:**

  1. **Session Meta:** Title (e.g., "2026 Q4 Equipment Verification").

  2. **Checklist Scope Filtering & Selection:**

     * Interactive serialized equipment table equipped with checkboxes and multi-select.

     * Filters:

       * Item Type / Category.

       * Custodian Department.

       * Custodian Attendance Status (e.g., target only personnel marked as `Present`, or selectively audit assets with holders marked as `Home/Remote`).

       * Current Physical Location.

     * "Select All Filtered" action with live counter: "Selected N items for audit".

  3. **Launch & Lock:**

     * "Launch Audit Session" action.

     * Atomic creation of session header and batch creation of all checklist records marked as pending.

     * Redirects to Active Audit Command Center (`/admin/audits/active`).

### Screen A-08: Transfer Templates Manager (`/admin/templates`)

* **Primary Goal:** Configure reusable item bundles to accelerate field transfers and onboarding/intake processes.

* **Components:**

  * Template listing: Name, Description, Preset Source/Destination (optional), Item Lines Count.

  * Create / Edit Template Dialog:

    * Template Name (e.g., "Standard Communications Kit").

    * Optional Preset Source Holder (e.g., "Central Storage").

    * Optional Preset Destination Holder.

    * Line Items Builder: Adds item definitions and corresponding quantities.

## 5. UI Error Handling, Form Rules & Edge Cases

| Area / Scenario | Condition | UI Behavior / Rule | 
| ----- | ----- | ----- | 
| **Authentication** | Email not in whitelist | Renders standard error view: "Your account is not authorized for this organization. Contact your administrator." | 
| **Location Integrity** | Setting a physical location | Validation enforces: At least one of Location Holder or Location Custom Text must be populated. | 
| **Transfer Intake** | Source holder is empty | Automatically marks transfer as Inbound Intake; enforces mandatory input in notes before commit. | 
| **Transfer Disposal** | Destination holder is empty | Automatically marks transfer as Outbound/Disposal; enforces mandatory input in notes before commit. | 
| **Cart Validation** | Transferring serialized item | Prevents staging if item is already staged or does not belong to designated source holder. | 
| **Field Scan** | No network connection | Displays non-blocking, prominent notification: "Network disconnected. Scan was not recorded. Tap to retry." | 
| **Audit Session** | Barcode scanned not in checklist | Emits distinct error alert and notification: "Item out of scope. Not included in this audit checklist." | 
| **Audit Resolution** | Resolving mismatch via transfer | Opens Transfer Approval Dialog; upon confirmation, generates transfer record and transitions audit row. | 
