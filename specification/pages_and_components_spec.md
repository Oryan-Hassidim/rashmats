# Detailed Pages & Components Specification - Rashmatz (v4 - Final)

This document fully specifies all pages of the **Rashmatz** system, the components required for each page, and their proposed implementation in **Microsoft FAST Element** under a **Multi-Page Application (MPA)** architecture, using the latest modern Web standards.

---

## 1. Cross-Cutting Architecture & Implementation Principles (Core UI & Modern Web Standards)

### 1.1 Separating `*.vm.ts` and `*.page.ts` for Unit Testing (Testability & Pure MVVM)
* Every page in the system is completely separated into 3 files under `src/pages/<route>/`:
  1. `index.html` – The static entry point for the page in the MPA.
  2. `<route>.vm.ts` (`PageViewModel`) – A TypeScript class completely decoupled from the DOM (`HTMLElement` / `FASTElement`), holding all reactive state (`@observable`), derived calculations (Getters), and asynchronous methods that communicate with `httpClient` and Services (injected via the Constructor with default values for easy mocking in Vitest without needing a DOM or browser).
  3. `<route>.page.ts` (`Page Component`) – The top-level `FASTElement` component for the page, holding an instance of `vm` (`readonly vm = new MyPageViewModel()`), calling `this.vm.init()` in `connectedCallback()`, and binding component events in the `html` template to `vm` methods (e.g., `@relocate="${(x, c) => c.parent.vm.relocate(c.event.detail)}"`).

### 1.2 Declarative HTML5 and Modern CSS Instead of Unnecessary JavaScript
1. **Semantic Elements and `data-*` Attributes Instead of Badge Components:**
   * Item status (`status`), attendance status (`attendance_status`), and transfer direction are rendered as `<span data-status="${x => x.status}">` or `<span data-attendance="${x => x.attendanceStatus}">` and styled directly in CSS under `@layer utilities`.
2. **Invoker Commands API (`commandfor` and `command`) for Opening and Closing `<dialog>`:**
   * Instead of writing JS methods that call `dialog.showModal()` or `dialog.close()` for modals and side drawers, the new HTML Invoker Commands standard is used:
     ```html
     <button type="button" commandfor="item-lifecycle-drawer" command="show-modal">Item History</button>
     <dialog id="item-lifecycle-drawer">
       <button type="button" commandfor="item-lifecycle-drawer" command="close">Close</button>
       ...
     </dialog>
     ```
3. **Smooth Page and Element Transitions (`View Transitions` + `@starting-style`):**
   * Navigation between MPA pages (and specifically between the 3 transfer steps `6A -> 6B -> 6C`) uses `@view-transition { navigation: auto; }` along with a shared `view-transition-name` for headers and the step bar.
   * Opening animations for `<dialog>` and `[popover]` are implemented in pure CSS using `@starting-style` and `transition-behavior: allow-discrete`.
4. **Light DOM by Default (`shadowOptions: null`):** All components and pages run in Light DOM. Only `<barcode-scanner-viewport>` is implemented with Shadow DOM to isolate the `<video>` element and the targeting reticle layer.

### 1.3 Smart Entity Component (`<entity-ref>`) with CSS Interest Invokers (`interestfor`)
* Everywhere in the system where a custody holder entity (`custody_holders` – whether a person `holder_type = 1` or a location/warehouse/vehicle `holder_type = 0`, including as a legal custodian or as the **physical location** of a serialized item) is displayed, the `<entity-ref :holderId="${x => x.holderId}">` component is used.
* **Caching All Holders in Client Memory (`holders_directory` with ETag/Hash):**
  * Since an organization contains up to a few hundred holders at most, the list of all holders in the organization is stored in `localStorage` under the key `rashmatz_holders_dir_${orgId}` along with `holders_directory_hash`.
  * On every page Bootstrap request, the client sends the `X-Holders-Hash` header. If the list has not changed on the server, the server returns `holders_directory: null` (saving 90% of the JSON payload size on every page navigation!), and the client uses the immediate cached list.
* **Popover Implementation via the Latest `interestfor` Standard + CSS Anchor Positioning:**
  * The component renders an element with the standard attribute `interestfor="entity-pop-${uid}"` (and a dynamic anchor name `style="anchor-name: --anchor-${uid}"`) alongside `<div popover="hint" id="entity-pop-${uid}" style="position-anchor: --anchor-${uid}">` with `interest-delay: 200ms 150ms`.
  * On hover (on desktop) or focus/long-press (on mobile), the entity details card pops up:
    * Full name, entity type (Person / Location), internal identifier, department, and attendance status (for a person).
    * If `phone_number` is defined for the entity, two quick action buttons are displayed in the card:
      1. **Direct Phone Call:** `<a href="tel:${phone}">📞 Call</a>`
      2. **Send WhatsApp Message:** `<a href="https://wa.me/${normalizedPhone}" target="_blank" rel="noopener">💬 Send WhatsApp</a>`

### 1.4 Unified Equipment View (Desktop vs. Mobile) and Grouping by Category
* **Unifying Serialized and Quantitative Items Under Category Grouping:** Across all equipment screens in the system (both in "My Custody" and in "Inventory & Catalog"), there is no separation into distinct "Serialized" vs. "Quantitative" tabs. All equipment is displayed in a unified list/table with a filter and search bar, and an option to group by **Category (`item_categories`)** using an accordion (`<details>`).
* **Units of Measure (`unit_of_measure`):** Defined as free text (`TEXT DEFAULT 'unit'`) entered by the admin when defining the item type (`item_types`).
* **Responsive Adaptation (Desktop Table vs. Mobile `<details><summary>`):**
  * **On Desktop:** Equipment is displayed in a dense, clear table, where grouping (by category, product type, or holder) wraps the table rows in an accordion.
  * **On Mobile:** Each item row itself is displayed as a collapsible `<details><summary>` element (showing the item name, barcode/quantity, and status in the `summary`, and the physical location, serial number, and actions when expanded).
  * **⚠️ Mandatory Implementation Note for the Coding Phase:** *"The creator has a specific code sample for the styling and structure of the mobile `<details><summary>` and accordion – **you must ask the user for that sample before writing the code for equipment lists and follow it precisely.**"*

### 1.5 Barcode Scanner Policy (`<barcode-scanner-viewport>`)
* **For Standard Users (Member):** Barcode scanning is performed **exclusively via the device camera** (`window.BarcodeDetector`). The component **does not** support external laser scanners and **does not** include a manual barcode entry field.
* **Preventing Duplicate Scans (Cooldown / Debounce):** The component enforces a `2000ms` cooldown on consecutive scans of the same barcode to prevent sending dozens of identical network requests while the camera is pointed at a sticker.
* **For Admins Only:** On admin pages (such as Quick Relocation `/items/relocate`), **outside** the barcode scanner component, an autocomplete search component (`<admin-item-search-box>`) is provided, allowing the admin to type and search for an item by barcode, serial number, internal identifier, or model name.

### 1.6 Client-Side Only Translation & Display Strings (Zero Server-Side Display Strings)
* The server **never returns display text strings** (neither in errors nor in successful responses). The server returns only numeric Enum codes, identifiers, and error values from a closed union type (`ApiError`). All text, labels, and messages are translated and rendered on the client side according to the UI language.

---

## 2. Global Shell & Navbar (`<app-navbar>`)

The global Navbar is rendered at the top of every authenticated page and receives its data from the `vm.navbar` object returned by the page's Bootstrap Endpoint.

### 2.1 Menu Structure: An Admin is First and Foremost a User!
* **Personal Area (always displayed to every authenticated user – both `Member` and `Admin`):**
  1. **My Custody (`/my-custody`)** – Equipment signed out in my name and equipment physically located with me.
  2. **My Transfers (`/my-transfers`)** – My personal transfer history.
  3. **Audit Scan (`/audit/scan`)** – Prominently displayed when an active audit exists in the organization (`active_audit_session !== null`).
* **Admin Area (additionally displayed for `role === 2` [Admin] only):**
  1. **Dashboard (`/admin/dashboard`)**
  2. **New Equipment Transfer (`/transfers/new`)**
  3. **Organization Transfers & Locations Log (`/admin/activity-logs`)**
  4. **Inventory & Catalog (`/admin/inventory`)**
  5. **Holders, Locations & Permissions (`/admin/holders`)**
  6. **Audits Center (`/admin/audits/active` or `/admin/audits/new`)**
  7. **Transfer Templates (`/admin/templates`)**
  8. **Quick Relocation (`/items/relocate`)**

### 2.2 Active Organization Switcher (`<tenant-switcher>`) and Creating a New Organization (Super-Admin Only):
* Displays the active organization and allows quick switching between organizations the user belongs to (`available_organizations`).
* **Creating a New Organization (Super-Admin Only):** If `navbar.user.is_super_admin === true`, a **`"+ Create New Organization"`** button (`commandfor="create-org-dialog" command="show-modal"`) appears at the bottom of the organization switcher menu.
  * The modal (`<dialog id="create-org-dialog">`) allows entering the organization name (`name`), path identifier (`slug`), and details of the first admin in the organization (by default: the Super-Admin themselves, or the email address and name of the new organization's admin), and sends `POST /organizations`.

### 2.3 Profile & Attendance Widget:
* Displays the user's name and avatar + attendance status tag (`<span data-attendance="...">`). For an admin, clicking the status opens a quick menu to change their personal attendance status (`Present` / `Home` / `Inactive`).

---

## 3. Detailed Specification for Every Page

---

### Page 1: Login & Smart Routing (`/login`)
* **Files:** `src/pages/login/index.html`, `login.page.ts`, `login.vm.ts`
* **Bootstrap Endpoint:** `GET /pages/login?return_to=<encoded_path>`
* **Behavior & Smart Routing (`LoginViewModel`):**
  1. **Full `return_to` Support:** If an unauthenticated user tried to access any internal page (e.g., `/transfers/new/parties` or `/my-custody`), they are redirected to `/login?return_to=%2Ftransfers%2Fnew%2Fparties`. After successful authentication via Google OAuth, the system returns them to the exact original `return_to` URL (after verifying it is a valid internal path).
  2. **Default Page When `return_to` is Absent:**
     * **For a standard user (`Member`):** If there is currently an active audit session in the organization (`active_audit_session_id !== null`), they are redirected directly to the audit scan page **`/audit/scan`**. If no audit is active, they are redirected to their personal equipment page **`/my-custody`**.
     * **For an admin (`Admin`):** Redirected to the dashboard `/admin/dashboard` (or to `/audit/scan` if an audit is open, according to the routing policy setting).
* **Page Components:**
  * Login card (`<article class="login-card">`) with a Google OAuth button.
  * Error box displayed if the code returned in `vm.error` is `EMAIL_NOT_WHITELISTED` or `ORGANIZATION_INACTIVE`.

---

### Page 2: My Custody & Physical Possession (`/my-custody`)
* **Files:** `src/pages/my-custody/index.html`, `my-custody.page.ts`, `my-custody.vm.ts`
* **Bootstrap Endpoint:** `GET /pages/my-custody`
* **Purpose & Innovations (`MyCustodyViewModel`):**
  * Allows the user to see in one place both **equipment signed out in their name under custody (`current_holder_id = me`)** and **equipment physically located with them (`location_holder_id = me`) even if it is signed out under someone else's name!**
  * Combines serialized items and quantitative items into a single list in `MyCustodyViewModel`, organized and grouped by **Category (`item_categories`)** with filtering and search run on the VM (and easy to test in Vitest).
* **Page Components & Structure:**
  1. **Custodian Header (`<header class="custodian-header">`):** Employee name, department, internal identifier, and `<span data-attendance="...">`.
  2. **Filter & Grouping Bar (`<search class="equipment-filter-bar">`):**
     * Free-text search (item name, barcode, serial number, location note).
     * Filter by category (`category_id`).
     * Filter by assignment type to me: `All` | `Signed out to me (In my custody)` | `Physically with me (Signed out to another)` | `Signed out to me but physically with another`.
     * Grouping toggle: `Group by category` (default: active).
  3. **Unified Equipment List (`<equipment-list-view>`):**
     * Grouped in `<details open>` accordions by category.
     * **On Desktop:** A table where each row displays: model name, type (serialized with barcode & serial number / quantitative with quantity & unit of measure), operational status (`<span data-status="...">`), **signed out to** (`<entity-ref :holderId="${x => x.current_holder_id}">`), and **current physical location** (`<location-display>`).
     * **On Mobile:** Each row is a compact `<details><summary>` (per the user's code sample).
  4. **Physical Location Indicator (`<location-display>`):**
     * If `location_holder_id` is set (can be a warehouse, vehicle, or **another person** who physically has the item!), renders `<entity-ref :holderId="${x => x.location_holder_id}">` so that on hover/tap you can view the person/location details and call or send a WhatsApp message directly!
     * Alongside it, displays `location_custom_text` (e.g., `"Glovebox"` or `"Locker 4"`).

---

### Page 3: Member Transfer History (`/my-transfers`)
* **Files:** `src/pages/my-transfers/index.html`, `my-transfers.page.ts`, `my-transfers.vm.ts`
* **Bootstrap Endpoint:** `GET /pages/my-transfers` (and for dynamic filtering via `MyTransfersViewModel`: `QUERY /transfers/my-query`)
* **Page Components & Structure:**
  1. **Filter Bar (`<search>`):** Search by voucher number/item, filter by direction (`All` | `Received` | `Handed Over`), and date range. Submitting the filter calls `vm.runQuery()`, which sends a `QUERY /transfers/my-query` request with a structured JSON body.
  2. **Transfer Voucher Cards (`<details class="transfer-voucher-card" name="my-transfers">`):**
     * In `<summary>`: voucher number (`TR-2026-...`), direction badge (`<span data-direction="...">`), counterparty (`<entity-ref :holderId="...">` with call/WhatsApp option in the Popover!), authorizing admin (`<entity-ref>`), and date/time.
     * In the `<details>` body: transfer notes (`notes`) and the list of transferred items (serialized and quantitative) grouped by category.

---

### Page 4: Field Audit Verification (`/audit/scan`)
* **Files:** `src/pages/audit-scan/index.html`, `audit-scan.page.ts`, `audit-scan.vm.ts`
* **Bootstrap Endpoint:** `GET /pages/audit-scan`
* **Data Structure and Calculations in `AuditScanViewModel` (Pre-closed Checklist Without Adding Records During Scan):**
  * An audit session is based on a pre-closed list of items (`audit_item_records` contains only statuses `0 = PENDING`, `1 = VERIFIED`, `2 = MISMATCH` – **without** adding new records during the session by scanners; any barcode not in the list returns a red `OUT_OF_SCOPE` error).
  * The server **does not** send redundant computed fields (such as percentages or remaining counts).
  * The server returns the active session details and the list of items relevant to the user (`my_audit_items`), where each item has its scan state:
    * `0` = Not yet scanned (`PENDING`)
    * `1` = Successfully scanned by me (`SCANNED_BY_ME`)
    * `2` = Scanned by someone else (`SCANNED_BY_OTHER` – includes `scanned_by_holder_id` to display via `<entity-ref>` who scanned the item!)
  * From this list, `AuditScanViewModel` computes on its own via pure Getters (easy to unit test): total (`totalCount`), scanned (`scannedCount`), remaining (`remainingCount`), and completion percentage (`progressPercentage`).
* **Page Components:**
  1. **`<barcode-scanner-viewport>` (Shadow DOM – top 45% of screen):**
     * Continuous camera scanning only + flashlight (Torch) toggle button. **No** external laser support and **no** manual barcode input field. Emits `@barcode-detected="${(x, c) => c.parent.vm.handleScan(c.event.detail.barcode)}"`.
  2. **Progress Meter & Last Scan Feedback:**
     * `<progress>` automatically computed from the VM, plus visual and audio/haptic feedback (`feedbackService`) for the scan result (`POST /audits/:sessionId/scan`).
  3. **My Audit Items List (bottom 55% of screen):**
     * Grouped by category or filtered by scan status (`Pending` | `Scanned by me` | `Scanned by another`).
     * If an item signed out in my name was scanned by someone else (`SCANNED_BY_OTHER`), the row displays `<entity-ref :holderId="${x => x.scanned_by_holder_id}">` so I can immediately see who has it and contact them!

---

### Page 5: Quick Physical Relocation (Admin) (`/items/relocate`)
* **Files:** `src/pages/relocate/index.html`, `relocate.page.ts`, `relocate.vm.ts`
* **Bootstrap Endpoint:** `GET /pages/items-relocate`
  * Returns `navbar` and the **20 most recent unique serialized items** relocated in the organization along with the latest relocation details for each (so if one of them is moved again, the admin can select it in one click without re-scanning).
* **Page Components:**
  1. **Item Lookup Area (Camera + Admin Search Outside Scanner):**
     * `<barcode-scanner-viewport>` for fast camera scanning.
     * **Outside the barcode scanner:** `<admin-item-search-box>` component (for Admins only!) allowing typing a barcode, serial number, or model name with autocomplete via `vm.searchItems(q)` (`QUERY /serialized-items/lookup` with `{ "q": "..." }`).
  2. **20 Recently Relocated Items List (`<recent-relocated-items-list>`):**
     * Displays the 20 most recent unique items and their latest location. Clicking an item in the list calls `vm.selectItem(item)` and immediately loads it into the relocation form.
  3. **Current State Card & New Location Form (`<relocation-form>`):**
     * Displays who the item is signed out to (`<entity-ref>` – read-only) and its current location.
     * **Selecting a New Location (Flexible OR Principle, enforced in `RelocateViewModel`):**
       * `to_location_holder_id`: Selection from all holders in the organization (already cached on the client in `holders_directory` – can be a **warehouse/vehicle** or a **person** who currently has/borrowed the item physically!).
       * `to_location_custom_text`: Free-text detail (e.g., `"Shelf 3"`).
       * `notes`: Relocation notes.

---

### Pages 6A, 6B, 6C: Modular Custody Transfer Flow in 3 Separate Pages (`/transfers/new/...`)

To keep things maximally simple, ensure precise data loading, and maintain a clean MPA flow, the transfer process is split into 3 sequential, clear pages communicating via URL parameters (`Query Parameters`):

```text
[6A: /transfers/new/template] ──> [6B: /transfers/new/parties?template_id=...] ──> [6C: /transfers/new/items?source_id=...&dest_id=...&template_id=...]
```

#### Page 6A: Step 1 – Choosing a Transfer Template or Free Transfer (`/transfers/new` or `/transfers/new/template`)
* **Files:** `src/pages/transfers-new-template/index.html`, `transfers-new-template.page.ts`, `transfers-new-template.vm.ts`
* **Business Logic:** Since a template (`transfer_templates`) can already contain default source holder (`default_source_holder_id`) and default destination holder (`default_dest_holder_id`) settings, choosing the template happens **before** choosing the parties!
* **Bootstrap Endpoint:** `GET /pages/transfers-new-template`
  * Returns `navbar` and the list of active template headers only (`id`, `name`, `description`, `default_source_holder_id`, `default_dest_holder_id`) – **without the line items of all templates** to save unnecessary traffic.
* **Page Flow:**
  * The admin can click **`"Start Free Transfer (No Template)"`** $\rightarrow$ navigates to `/transfers/new/parties`.
  * Or select one of the templates in the list $\rightarrow$ navigates to `/transfers/new/parties?template_id=<uuid>`.
  * *(If an admin arrived with `?preselect_item_id=<uuid>` from another screen such as an audit or catalog, the parameter is preserved in the URL across the steps).*

#### Page 6B: Step 2 – Selecting Transfer Parties: Source and Destination (`/transfers/new/parties`)
* **Files:** `src/pages/transfers-new-parties/index.html`, `transfers-new-parties.page.ts`, `transfers-new-parties.vm.ts`
* **Supported Query Params:** `?template_id=<uuid>&preselect_item_id=<uuid>`
* **Bootstrap Endpoint:** `GET /pages/transfers-new-parties?template_id=<uuid>`
  * Returns `navbar` (relying on the full organization `holders_directory` cached on the client). If `template_id` is sent, returns the template header including `default_source_holder_id` and `default_dest_holder_id` so `TransfersNewPartiesViewModel` pre-populates them automatically in the selectors!
* **Page Components & Flow:**
  * Transfer mode selection:
    1. `Internal Transfer`: Select source (`source_holder_id`) and destination (`dest_holder_id`) from the organization's holders (people or locations, with `<entity-ref>`).
    2. `External Intake`: Source is `null` (adding new inventory to the organization), select destination only.
    3. `Disposal / External Outbound`: Select source only, destination is `null`.
  * Clicking `"Continue to Select Items & Quantities"` navigates to:
    `/transfers/new/items?source_id=<uuid|null>&dest_id=<uuid|null>&template_id=<uuid|empty>`

#### Page 6C: Step 3 – Selecting Items, Quantities, Before/After Balance Indicators, Physical Location Sync, and Confirmation (`/transfers/new/items`)
* **Files:** `src/pages/transfers-new-items/index.html`, `transfers-new-items.page.ts`, `transfers-new-items.vm.ts`
* **Query Params:** `?source_id=<uuid|null>&dest_id=<uuid|null>&template_id=<uuid|empty>&preselect_item_id=<uuid|empty>`
* **Smart & Focused Bootstrap Endpoint:** `GET /pages/transfers-new-items?source_id=...&dest_id=...&template_id=...`
  * **Instead of blindly loading every product type in the world:**
    1. If there is a **source holder (`source_id !== null`)**: The server returns **all equipment currently registered under the source** (both serialized items in their custody and their quantitative stock balances).
    2. If there is a **destination holder (`dest_id !== null`)**: The server also returns the **current quantitative and serialized balances at the destination** (for those same item types), so `TransfersNewItemsViewModel` can calculate and display a full comparative indicator!
    3. If **`template_id`** was passed: The server now fetches and returns (and only for the selected template!) the **template lines (`selected_template.lines`)**, and the VM automatically checks/populates the requested quantities from the source's inventory.
    4. **Only if the source is `null` (`Intake` mode – external intake):** The server returns the organization's item types catalog (`item_types`) and categories (`item_categories`), so the admin can choose an existing item type for intake and avoid accidentally creating a duplicate of an existing product type.
* **Components & Capabilities on Page 6C:**
  1. **Top Parties Summary Bar:** Displays Source (`<entity-ref>`) $\rightarrow$ Destination (`<entity-ref>`), and a back link to change parties.
  2. **Items & Quantities Selection Table with Full Balance Indicators (Before / After):**
     * Source items are displayed grouped by **Category** (with filtering and search, or typing/scanning a barcode to quickly select a serialized item from the source's list).
     * For every product row (quantitative or group of serialized items of the same model), `TransfersNewItemsViewModel` computes in real time with every change in transfer quantity ($Q_{\text{transfer}}$):
       * **At Source:** `Currently available: X` $\rightarrow$ **`Remaining after transfer: X - Q`**
       * **At Destination:** `Currently held: Y` $\rightarrow$ **`Held after transfer: Y + Q`**
     * For serialized items: selection via checkboxes or barcode scanning that automatically checks the item from the source's list.
  3. **Adding a New Item Type to the Catalog & Intake of New Items (in `Intake` mode when `source_id === null`):**
     * A **`"+ Add New Item Type to Catalog"`** button that opens a `<dialog>` or expandable row to create a new `item_type` (model name, existing or new category selection, serialized/quantitative tracking type, unit of measure as free text, and description) and saves it on the server (`POST /item-types`) or as part of the transfer.
     * For intake of new serialized items into the organization (`Intake`): when entering/scanning the new barcode, the VM calls `QUERY /serialized-items/check-unassigned`, which verifies that the barcode or serial number is **not already registered under anyone in the system** to prevent duplicates.
  4. **Physical Location Sync Control on Transfer (`Physical Location Sync Option`):**
     * In the summary area before submission, a Checkbox is displayed (checked by default: `update_physical_location: true`):
       * **When checked (`true`):** The physical location of transferred serialized items is automatically updated to the destination holder (`location_holder_id = dest_holder_id`), with an option to also enter a specific location (another entity and/or `location_custom_text`), and a corresponding movement is recorded in `serialized_item_location_logs`.
       * **When unchecked (`false`):** Only legal custody (`current_holder_id`) transfers to the destination, while the current physical location of the items (e.g., a vehicle or warehouse where they are stored) remains unchanged!
  5. **Notes Field & Confirmation (`POST /transfers` with `Idempotency-Key`):**
     * `notes` field (mandatory if `source_id === null` or `dest_id === null`).
     * Execute transfer button that generates the voucher and displays a success `<dialog>` with the voucher number and a text share button (Clipboard / WhatsApp).

---

### Page 7: Active Audit Command Center (`/admin/audits/active`)
* **Files:** `src/pages/admin-audit-active/index.html`, `admin-audit-active.page.ts`, `admin-audit-active.vm.ts`
* **Bootstrap Endpoint:** `GET /pages/admin-audits-active`
* **Hybrid Live Update Mechanism in `AdminAuditActiveViewModel` (SSE + Polling Fallback / Reconnect):**
  1. **Primary Mode – Server-Sent Events (`SSE`):** When the page loads, the VM opens `new EventSource('https://api.../audits/:sessionId/stream', { withCredentials: true })`. On every field scan, the server pushes a `scan_updated` event with the updated row (including an event ID `id: <timestamp_or_seq>` for `Last-Event-ID`).
  2. **Disconnect Recovery & Automatic Polling Fallback:**
     * If the SSE connection drops (`onerror`), the browser automatically attempts to reconnect the SSE request (with `Last-Event-ID` to catch up on scans that occurred during the disconnection).
     * If the SSE reconnection attempt fails (or while the connection remains disconnected for over 5 seconds), `AdminAuditActiveViewModel` automatically switches to **Fallback Polling** mode (`GET /audits/:sessionId/live-status?since=...` every 10 seconds while the tab is visible), and concurrently tries to re-establish SSE in the background. As soon as SSE reconnects (`onopen`), Polling stops automatically!
* **Audit Discrepancy Logic (`AdminAuditActiveViewModel`):**
  * When an item is scanned in an audit by someone other than its registered holder (`scan_status = 2 MISMATCH`), the admin sees the registered holder (`<entity-ref>`) and the actual scanner (`<entity-ref>`) on the discrepancy card.
  * **No automatic custody transfer via `resolve`!** Instead, the discrepancy card has 3 clear, separate actions:
    1. **Quick Physical Relocation (`Quick Relocate`):** Calls `vm.quickRelocateToScanner(record)`, which updates only the physical location of the item (`POST /serialized-items/:id/relocate`) to point to the actual scanner (`to_location_holder_id = scanned_by_holder_id`), without changing legal custody.
    2. **Go to Full Custody Transfer Page:** A link button leading to the transfers page with parameters pre-filled in the URL:
       `/transfers/new/items?source_id=${expectedHolderId}&dest_id=${scannedByHolderId}&preselect_item_id=${serializedItemId}`
    3. **Resolve Discrepancy with Local Audit Note (`Resolve with Note`):** Calls `vm.resolveWithNote(recordId, notes)` (`POST /audits/:sessionId/records/:recordId/resolve`) and saves **only** a local audit note (`resolution_notes` and `resolved_by_admin_id`) in the `audit_item_records` table.

---

### Page 8: Executive Dashboard (`/admin/dashboard`)
* **Files:** `src/pages/admin-dashboard/index.html`, `admin-dashboard.page.ts`, `admin-dashboard.vm.ts`
* **Bootstrap Endpoint:** `GET /pages/admin-dashboard`
* **Components:**
  1. Active audit banner (if open) with a link to `/admin/audits/active`.
  2. Serialized inventory readiness cards by status + low quantitative stock alerts.
  3. Personnel attendance breakdown (`Present` / `Home` / `Inactive`).
  4. Last 10 transfer vouchers and physical relocations in the organization (with `<entity-ref>` on every party and admin).

---

### Page 9: Organization Custody Transfers & Locations Activity Log (`/admin/activity-logs`)
* **Files:** `src/pages/admin-activity-logs/index.html`, `admin-activity-logs.page.ts`, `admin-activity-logs.vm.ts`
* **Purpose:** A dedicated admin page for viewing, searching, and filtering **custody transfer history (`custody_transfers`)** as well as **physical location movement history (`serialized_item_location_logs`)** across the entire organization.
* **Bootstrap Endpoint:** `GET /pages/admin-activity-logs` (and for filtering via `AdminActivityLogsViewModel`: `QUERY /activity-logs/query`)
* **Page Components:**
  1. **View Toggle / Log Type Filter:** `All Movements (Combined)` | `Custody Transfer Vouchers (Transfers)` | `Physical Relocation Log (Relocations)`.
  2. **Advanced Filter Bar (`<search>`):** Filter by involved holder/location (`holder_id`), executing admin (`admin_holder_id`), specific category/item, and date range.
  3. **Log Table / List:**
     * In **Custody Transfer** rows: displays voucher number (`TR-2026-...`), source (`<entity-ref>`) $\rightarrow$ destination (`<entity-ref>`), authorizing admin (`<entity-ref>`), notes, and an expandable `<details>` to view all items in the voucher.
     * In **Physical Relocation** rows: displays the serialized item (name, barcode, serial number), who it is signed out to (`<entity-ref>`), previous location (`<entity-ref>` + text) $\rightarrow$ new location (`<entity-ref>` + text), executing admin (`<entity-ref>`), and notes.

---

### Page 10: Inventory & Equipment Catalog (`/admin/inventory`)
* **Files:** `src/pages/admin-inventory/index.html`, `admin-inventory.page.ts`, `admin-inventory.vm.ts`
* **Bootstrap Endpoint:** `GET /pages/admin-inventory`
* **Views, Grouping & Filtering Structure (`AdminInventoryViewModel`):**
  1. **Universal Filter Bar (active across all views – including grouped views!):**
     * Text search, filter by category (`category_id`), tracking type (All / Serialized / Quantitative), operational status, department, custody holder, and physical location.
     * When the admin applies a filter in a grouped view (e.g., grouped by holder or by category), `AdminInventoryViewModel` filters the group tree in real time and displays only groups and items matching the search!
  2. **Grouping Mode Selector:**
     * `By Category & Product Type` (`<details>` accordion of Category $\rightarrow$ Product Type $\rightarrow$ serialized details and quantitative balances).
     * `By Holder / Location` (accordion per holder showing their equipment grouped by category, distinguishing between equipment signed out to them and equipment physically located with them).
     * `Flat List (by Category)`.
  3. **Responsive Rendering:**
     * **On Desktop:** Full table where grouping uses accordion rows.
     * **On Mobile:** Each item row is a collapsible `<details><summary>` (per the user's code sample).
  4. **Item Lifecycle Drawer (`<dialog id="item-lifecycle-drawer">`):**
     * Displays the full history of a serialized item (`GET /serialized-items/:id/lifecycle`) + quick navigation button to custody transfer (`/transfers/new/parties?preselect_item_id=...`) or relocation (`/items/relocate?item_id=...`).

---

### Page 11: Holders, Locations, Departments & Certifications Management (`/admin/holders`)
* **Files:** `src/pages/admin-holders/index.html`, `admin-holders.page.ts`, `admin-holders.vm.ts`
* **Bootstrap Endpoint:** `GET /pages/admin-holders`
* **Components & Capabilities (`AdminHoldersViewModel`):**
  1. **Tabs:** `People (holder_type = 1)` | `Locations (holder_type = 0)` | `Departments & Certifications Catalog`.
  2. **Inline Attendance Management:** Changing the attendance `<select>` (`Present` / `Home` / `Inactive`) calls `vm.updateAttendance(holderId, status)` (`PATCH /holders/:id/attendance`).
  3. **Full Holder Details & Inventory Drawer (`GET /holders/:id/details`):**
     * Displays separately and clearly:
       * **Equipment signed out in the holder's name (`custody_items` – serialized and quantitative)** grouped by category.
       * **Equipment physically located with the holder but signed out to someone else (`physically_located_items`)** with `<entity-ref>` of the signed-out owner!
     * Management of employee certificates and certifications (`holder_certifications`: add `POST`, update expiration/notes `PUT`, and delete `DELETE`).

---

### Page 12: Audit Session Setup Wizard (`/admin/audits/new`)
* **Files:** `src/pages/admin-audit-new/index.html`, `admin-audit-new.page.ts`, `admin-audit-new.vm.ts`
* **Bootstrap Endpoint:** `GET /pages/admin-audits-new`
* **Components (`AdminAuditNewViewModel`):**
  * Entering the audit session title.
  * Filtering serialized items by category, model, department, **holder attendance status** (`Present` / `Home` / `Inactive`), and physical location, with the list grouped in an accordion by **Category**.
  * Selecting items and launching the session (`POST /audits`).

---

### Page 13: Transfer Templates Manager (List + Edit on the Same Page) (`/admin/templates`)
* **Files:** `src/pages/admin-templates/index.html`, `admin-templates.page.ts`, `admin-templates.vm.ts`
* **Bootstrap Endpoint:** `GET /pages/admin-templates`
* **Page Components (`AdminTemplatesViewModel`):**
  1. List of existing template cards (name, description, `<entity-ref>` for default source/destination if defined, and a `"Start Transfer from This Template"` button leading to `/transfers/new/parties?template_id=...`).
  2. Clicking `"Edit Template"` or `"+ New Template"`: opens `<dialog id="template-editor-dialog">` on the same page (which fetches the template lines via `vm.openEditDialog(id)` from `GET /transfer-templates/:id` if editing) and allows quick saving (`POST /transfer-templates` or `PUT /transfer-templates/:id`).

---

## 4. Summary Table: Mapping of Pages, VM Files & Bootstrap Endpoints

| # | Client Page Route (MPA Route) | Page Files (`src/pages/...`) | Permission | Bootstrap Endpoint |
| :--- | :--- | :--- | :--- | :--- |
| **1** | `/login` | `index.html`, `login.page.ts`, `login.vm.ts` | Public | `GET /pages/login` |
| **2** | `/my-custody` | `index.html`, `my-custody.page.ts`, `my-custody.vm.ts` | Member / Admin | `GET /pages/my-custody` |
| **3** | `/my-transfers` | `index.html`, `my-transfers.page.ts`, `my-transfers.vm.ts` | Member / Admin | `GET /pages/my-transfers` |
| **4** | `/audit/scan` | `index.html`, `audit-scan.page.ts`, `audit-scan.vm.ts` | Member / Admin | `GET /pages/audit-scan` |
| **5** | `/items/relocate` | `index.html`, `relocate.page.ts`, `relocate.vm.ts` | Admin | `GET /pages/items-relocate` |
| **6A** | `/transfers/new/template` | `index.html`, `transfers-new-template.page.ts`, `transfers-new-template.vm.ts` | Admin | `GET /pages/transfers-new-template` |
| **6B** | `/transfers/new/parties` | `index.html`, `transfers-new-parties.page.ts`, `transfers-new-parties.vm.ts` | Admin | `GET /pages/transfers-new-parties` |
| **6C** | `/transfers/new/items` | `index.html`, `transfers-new-items.page.ts`, `transfers-new-items.vm.ts` | Admin | `GET /pages/transfers-new-items` |
| **7** | `/admin/audits/active` | `index.html`, `admin-audit-active.page.ts`, `admin-audit-active.vm.ts` | Admin | `GET /pages/admin-audits-active` |
| **8** | `/admin/dashboard` | `index.html`, `admin-dashboard.page.ts`, `admin-dashboard.vm.ts` | Admin | `GET /pages/admin-dashboard` |
| **9** | `/admin/activity-logs` | `index.html`, `admin-activity-logs.page.ts`, `admin-activity-logs.vm.ts` | Admin | `GET /pages/admin-activity-logs` |
| **10** | `/admin/inventory` | `index.html`, `admin-inventory.page.ts`, `admin-inventory.vm.ts` | Admin | `GET /pages/admin-inventory` |
| **11** | `/admin/holders` | `index.html`, `admin-holders.page.ts`, `admin-holders.vm.ts` | Admin | `GET /pages/admin-holders` |
| **12** | `/admin/audits/new` | `index.html`, `admin-audit-new.page.ts`, `admin-audit-new.vm.ts` | Admin | `GET /pages/admin-audits-new` |
| **13** | `/admin/templates` | `index.html`, `admin-templates.page.ts`, `admin-templates.vm.ts` | Admin | `GET /pages/admin-templates` |
