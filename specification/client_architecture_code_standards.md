# Rashmatz Client Specification & Coding Rules

Strict guidelines for LLM code generation. Adhere strictly to the directives, contracts, and patterns below.

---

## 1. Core Architecture & Tech Stack

* **Architecture:** Multi-Page Application (MPA). Discrete static HTML entry points per route (`src/pages/**/index.html`). No client-side SPA routing.
* **Component Library:** Microsoft FAST Element (`@microsoft/fast-element`).
* **Toolchain:** **Vite+** (`vite-plus` / VoidZero: Rolldown, Oxlint, Oxfmt, Vitest) via a unified `vite.config.ts`.
* **TypeScript:** Strict mode enabled (`strict: true`, `noImplicitAny: true`, `exactOptionalPropertyTypes: true`, `noUncheckedIndexedAccess: true`).
* **Dependency Minimization:** Standard Web APIs first. Zero external utility libs (`date-fns`, `lodash`, etc., are strictly prohibited).

---

## 2. Directory Structure

```text
src/
├── shared/
│   ├── api/          # http-client.ts (Result pattern), types.ts (DTOs)
│   ├── auth/         # session.service.ts (cookie & tenant state)
│   ├── services/     # barcode-scanner.ts, feedback.service.ts
│   ├── utils/        # temporal.ts, view-transitions.ts
│   └── styles/       # layers.css, tokens.css, base.css
├── components/       # Reusable FAST elements (ui/, items/)
└── pages/            # MPA route modules (HTML + *.page.ts + *.vm.ts)
```

---

## 3. DOM & CSS Guidelines

* **Shadow DOM Policy:** Default to **Light DOM** (`shadowOptions: null`) for all structural, page, table, and list components so global styles cascade. Restrict Shadow DOM exclusively to isolated leaf widgets (e.g., camera viewports).
* **CSS Scoping:** In Light DOM, scope rules directly to the custom element tag name. Do not use BEM or manual prefixing:
  ```css
  @layer components {
    item-card {
      display: block;
      padding: var(--space-md);
      header { display: flex; justify-content: space-between; }
    }
  }
  ```
* **CSS Layers:** Strictly enforce precedence order: `@layer reset, tokens, base, components, utilities;`.
* **Semantic HTML5:**
  * Modals: Native `<dialog>` via `.showModal()` and `.close()`.
  * Accordions / Exclusive Groups: `<details name="group-key">` with `<summary>`.
  * Search: Enclose inputs inside `<search>`.
  * Structure: Use `<header>`, `<main>`, `<nav>`, `<section>`, `<article>`, `<aside>`, `<form>`.

---

## 4. MVVM & Event Wiring Pattern

* **Single Source of Truth:** Exactly one `PageViewModel` class per page. Holds all mutable state (`@observable`) and performs async API operations.
* **Presentational Components:** Inherit from `FASTElement`, receive data via `@observable` bindings, and emit events (`this.$emit('event-name', payload)`). Never invoke services or API directly.
* **Parent-Child Event Binding:** The parent page template delegates component events directly to the VM using the FAST context object:
  ```typescript
  // In Page Template:
  html<MyPage>`
    <item-card 
      :item="${x => x}" 
      @relocate="${(x, c) => c.parent.vm.relocate(c.event.detail)}">
    </item-card>
  `
  ```

---

## 5. Web APIs & Hardware Integrations

### 5.1 View Transitions
* **Cross-Document:** CSS-driven: `@view-transition { navigation: auto; }`.
* **Element / Scoped Transitions Utility (`src/shared/utils/view-transitions.ts`):**
  ```typescript
  export type TransitionTarget = Document | Element | null;

  export async function runViewTransition(
    target: TransitionTarget,
    mutation: () => void | Promise<void>
  ): Promise<void> {
    const scope = target ?? document;
    if ('startViewTransition' in scope && typeof scope.startViewTransition === 'function') {
      const vt = (scope as { startViewTransition: (cb: () => void | Promise<void>) => { finished: Promise<void> } }).startViewTransition(mutation);
      await vt.finished;
    } else {
      await mutation();
    }
  }
  ```

### 5.2 Date & Time
* Use `@js-temporal/polyfill` (`Temporal.Instant`, `Temporal.PlainDate`) exclusively. Format output via native `Intl.DateTimeFormat`.

### 5.3 Barcode Detection
* Primary: Native `window.BarcodeDetector` (`code_128`, `qr_code`, `ean_13`).
* Fallback: Dynamically import a polyfill only when `window.BarcodeDetector` is absent.

### 5.4 Audio & Haptic Feedback
* Zero downloaded media files. Use synthetic Web Audio (`AudioContext` sine tones) and `navigator.vibrate`:
  * **Success:** 880Hz (80ms) + 80ms vibration.
  * **Mismatch / Alert:** 440Hz double pulse + `[100, 50, 100]` vibration.
  * **Error:** 220Hz low tone (250ms) + 200ms vibration.

---

## 6. HTTP, Result Pattern & Tenancy

### 6.1 Strict Result Type & HTTP Client (`src/shared/api/http-client.ts`)
Do not throw exceptions for HTTP or network failures. Return typed union:
```typescript
export interface ApiError {
  status: number;
  code: string;
  message: string;
  isNetworkError: boolean;
}

export type Result<T, E = ApiError> = 
  | { ok: true; data: T } 
  | { ok: false; error: E };
```
* The `httpClient` methods (`get`, `post`, `put`, `delete`) must include `{ credentials: 'include' }` on all requests and resolve to `Promise<Result<T, ApiError>>`.

### 6.2 Tenancy & Session State
* **Auth Session:** Backend-managed secure cookies (`HttpOnly`, `SameSite=Lax`).
* **Active Tenant:** Stored in a client-readable cookie or `localStorage` (`active_org_id`). Org switches simply update the stored ID and reload context.
* **Transient Workflows:** Multi-step wizard drafts (e.g., Transfer Cart) persist in `sessionStorage` until committed.