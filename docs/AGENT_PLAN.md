# LekhaBook: Agent Execution Plan

> **Who this is for:** a coding agent working in this repository.
> **What it delivers:** LekhaBook, a mobile-first bookkeeping app for **Party ledgers, Bank accounts and Petty Cash**, with photo or file proof attached to entries.
> **How it is built:** Blazor UI shared across Android, iOS, Windows and the browser. It works offline on a local SQLite database and auto-syncs with an online PostgreSQL server through an API.
> **Reference product:** https://my.karobarapp.com/ (Karobar). We copy its **workflow and usability**. We never copy its branding, logo, text, images or code.

Work through the phases **in order**. Each phase lists what you **do**, what you **don't do**, and its **exit criteria**. Don't start a phase until the previous phase's exit criteria pass. When a decision in [§ 10](#10-decisions-recommended-defaults) is still open, use its default and note it in `docs/DECISIONS.md`.

---

## 0. Ground rules (apply to every phase)

1. **Verify library facts, don't recall them.** Use Context7 (or official docs) before you pin any version or API: .NET, MAUI, MudBlazor, EF Core, Npgsql, Playwright. Record pinned versions in `Directory.Packages.props`.
2. **The target is .NET 10 (LTS).** Check the current LTS first. Use the `maui-blazor-web` template (verify it with `dotnet new list maui`).
3. **Use a classic `.sln` file** (`dotnet new sln --format sln`) so that Visual Studio 2022+, Rider and VS Code C# Dev Kit all open it.
4. **Don't commit secrets.** Connection strings and JWT keys go in `appsettings.Development.json`, which is git-ignored, or in user-secrets. Commit `appsettings.Example.json`.
5. **Write small commits per feature on a branch.** Never push to `main` without the user's OK.
6. **Money is `decimal(18,2)`.** Never use `double` or `float`.
7. **Never store or sync balances.** Always compute them from transactions (see [§ 5.3](#53-balances)).
8. **Every table except global ones (Users) carries `CompanyId`,** and the server enforces it.

---

## Phase 1: Discover the reference app with Microsoft Edge (read-only)

### 1.1 Browser setup (Edge through Playwright MCP)

Neither the built-in browser pane nor the Chrome extension is Edge. Use **Playwright MCP on the Edge channel** with a persistent profile so the user's login survives restarts.

Add this to the project's `.mcp.json`:

```json
{
  "mcpServers": {
    "edge": {
      "command": "npx",
      "args": [
        "@playwright/mcp@latest",
        "--browser", "msedge",
        "--user-data-dir", "./.edge-profile",
        "--viewport-size", "390,844"
      ]
    }
  }
}
```

- First run `npx @playwright/mcp@latest --help` and confirm the flag names and the `--viewport-size` format. Adjust the config if they differ.
- Add `.edge-profile/` to `.gitignore`. It holds session cookies.
- Explore **twice**: first at a phone viewport (390×844), then at desktop size (1440×900). Reopen with a different `--viewport-size`, or use the resize tool.

### 1.2 Login (the user does this, the agent doesn't)

The reference app signs in with **+977 phone number and OTP**, **Google**, or **QR code**.

- Open `https://my.karobarapp.com/login` in the Edge session, then **stop and ask the user to sign in themselves** in that window.
- **Never** type a phone number, OTP, Google password or QR approval. Wait until the user says "done".
- Ask the user which **business** to explore. If they agree, they can create a throwaway test business; that is the only place where saving is allowed.

### 1.3 Safety rules during discovery

| Allowed | Not allowed |
|---|---|
| Open every menu, tab, dialog and form | Saving forms in a real business |
| Open dropdowns and date pickers; read validation messages by tabbing through empty required fields | Deleting anything |
| Save test entries **only in the approved test business** | Sending WhatsApp or SMS reminders, which reach real people |
| Take screenshots, read the DOM and record network calls (URLs and shapes, no tokens) | Changing account, subscription or staff settings |
| | Copying customer names or phone numbers into the repo, because they are personal data |

### 1.4 What to capture

Walk **every** menu item. For each **screen** or **form**, write one file at `docs/discovery/<NN>-<slug>.md` using this template:

```markdown
# <Screen name>
- Path / menu: Home > Parties > Add Party
- Purpose:
- Viewport notes (mobile vs desktop differences):
- Screenshot: ./img/<NN>-<slug>-mobile.png, ./img/<NN>-<slug>-desktop.png
## Fields
| Label | Type | Required | Default | Validation / limits | Notes |
## Actions / buttons (and keyboard shortcuts, if any)
## Workflow (step-by-step, incl. what happens after Save)
## Lists / filters / search / sort / date-range
## Reports or totals shown and how they are calculated
## Attachments (camera / gallery / file? size? count?)
## Edge cases observed (empty state, errors, offline banner)
```

Mask personal data in screenshots: blur or crop names and phone numbers.

**Checklist. Confirm each item explicitly** in `docs/discovery/00-summary.md`:

- [ ] Full menu tree (sidebar, bottom nav, settings), at both viewports
- [ ] Business/company: create, switch, edit profile, fiscal year
- [ ] Staff/multi-user: invite flow, roles, and the permission matrix for each module
- [ ] Parties: customer vs supplier, add/edit fields, opening balance (receivable/payable), ledger view, statement, reminders
- [ ] Transactions: "You Gave / You Got" style or receipt/payment, cash in/out, expense, and any transfer between accounts
- [ ] Bank accounts: add, opening balance, deposit, withdraw, transfer, cheque fields (if any)
- [ ] Cash / petty cash: in, out, adjust, cash-in-hand
- [ ] Attachments: where they can be added, camera vs gallery, preview, delete
- [ ] Reports: party statement, day book, cash book, bank book, summary, export formats (PDF/Excel), share
- [ ] Search, filters, date ranges, sorting
- [ ] **Localization:** English/Nepali toggle, **BS (Bikram Sambat) vs AD dates**, NPR formatting (lakh grouping `1,00,000.00`)
- [ ] Offline behavior of the web version, if any
- [ ] Keyboard behavior on desktop web (Tab/Enter, shortcuts)
- [ ] Out-of-scope modules (sales, purchase, inventory, invoices): **list them only**, with one line each, for a future version

### 1.5 Phase 1 outputs and exit criteria

- `docs/discovery/00-summary.md`: menu tree, module list, and a scope table (In v1 / Later / Not doing).
- `docs/discovery/NN-*.md`: one file per screen.
- `docs/discovery/workflows.md`: the main end-to-end flows written as numbered steps (add party → record payment with photo → view ledger → share statement).
- `docs/discovery/gap-notes.md`: things that are confusing or slow in the reference app that we will do better.
- **Exit:** the user reviews `00-summary.md` and approves the v1 scope.

---

## 2. Scope of v1

> ✅ **Approved by the user on 2026-11-23.** The detailed scope table is in [discovery/00-summary.md § 4](discovery/00-summary.md#4-proposed-v1-scope-for-approval). Where the list below differs from it, the table wins.

**In scope (v1):**
1. Auth, companies, company users and permissions
2. Party ledger (customers and suppliers): receivable/payable, opening balance, statement
3. Bank accounts: deposit, withdrawal, transfer, statement
4. Petty cash / cash in hand: in, out, statement
5. Transfers between cash and bank (contra)
6. Attachments on every transaction: camera, gallery or file
7. Reports: party statement, cash book, bank book, day book, and a dashboard with totals; PDF export and share
8. Offline-first on Android, iOS and Windows, with auto-sync
9. Browser version with the same UI and workflow (online in v1; see [D1](#10-decisions-recommended-defaults))

**Out of scope for v1** (design must not block these): sales and purchase invoices, inventory, tax/VAT, SMS/WhatsApp reminders, multi-currency.

---

## 3. Solution architecture

### 3.1 Projects (one `LekhaBook.sln`)

```
LekhaBook.sln
├─ src/
│  ├─ LekhaBook.Domain/          # Entities, enums, business rules (no EF, no UI), net10.0
│  ├─ LekhaBook.Contracts/       # DTOs + sync contracts shared by API and clients
│  ├─ LekhaBook.Application/     # Use-cases/services (PostTransaction, GetLedger…), validation (FluentValidation)
│  ├─ LekhaBook.Data/            # EF Core model shared by both databases + balance queries (depends on Domain only)
│  ├─ LekhaBook.Data.Sqlite/     # EF Core SQLite DbContext + local repositories + outbox (client side)
│  ├─ LekhaBook.Data.Postgres/   # EF Core Npgsql DbContext + migrations (server side)
│  ├─ LekhaBook.UI/              # Razor Class Library: ALL pages, components, layouts, JS (enter-nav), CSS
│  ├─ LekhaBook.Client/          # API client + session (ISessionService, ICompanyApi) for both hosts; hosts plug in token/settings storage
│  ├─ LekhaBook.Api/             # ASP.NET Core Web API: auth, sync, attachments, reports
│  ├─ LekhaBook.Web/             # Blazor Web App host for browsers (uses LekhaBook.UI)
│  └─ LekhaBook.App/             # .NET MAUI Blazor Hybrid host: Android, iOS, Windows (uses LekhaBook.UI)
├─ tests/
│  ├─ LekhaBook.Domain.Tests/
│  ├─ LekhaBook.Application.Tests/
│  ├─ LekhaBook.Data.Tests/      # migrations on real SQLite + PostgreSQL; SQL sums = domain balances
│  ├─ LekhaBook.Sync.Tests/      # two simulated devices + server, conflict scenarios
│  └─ LekhaBook.Api.IntegrationTests/  # Testcontainers PostgreSQL
├─ docs/ (this plan, discovery, decisions)
├─ docker-compose.yml           # postgres:17 + pgadmin for local dev
├─ Directory.Build.props        # nullable, warnings-as-errors, LangVersion
├─ Directory.Packages.props     # central package versions
├─ global.json                  # pin SDK
├─ .vscode/ (launch.json, tasks.json, extensions.json)
└─ .editorconfig
```

### 3.2 Hosting abstraction (makes the browser work like the app)

`LekhaBook.UI` must depend **only on interfaces**, never on SQLite, MAUI or HttpClient directly:

| Interface | MAUI app (`LekhaBook.App`) | Browser (`LekhaBook.Web`) |
|---|---|---|
| `IDataService` (parties, accounts, transactions, reports) | Local SQLite through `Data.Sqlite`, plus the outbox | HTTP calls to `LekhaBook.Api` |
| `IAttachmentService` (capture, pick, store, get) | MAUI `MediaPicker` (camera/gallery) and `FilePicker`, saved to app data, upload queued | `<InputFile accept="image/*,application/pdf" capture="environment">` with direct upload |
| `IConnectivityService` | MAUI `Connectivity` | `navigator.onLine` through JS interop |
| `ISessionService` (current user, company, token, PIN lock) | `SecureStorage` | Auth cookie or token in memory |
| `ISyncService` | Real background sync | No-op (always online) |
| `IShareService` (share a PDF statement) | MAUI `Share` | Browser download, or `navigator.share` |

The same Razor pages run in both hosts. Only the DI registrations differ.

### 3.3 Tech choices (confirm versions with Context7 before you pin them)

- **UI:** MudBlazor (v9.x at the time of writing) for responsive, touch-friendly Material components. Alternative in [D3](#10-decisions-recommended-defaults).
- **Validation:** FluentValidation, shared by client and server.
- **ORM:** EF Core 10 with `Microsoft.EntityFrameworkCore.Sqlite` (client) and `Npgsql.EntityFrameworkCore.PostgreSQL` (server).
- **Auth:** ASP.NET Core Identity with JWT access tokens and rotating refresh tokens.
- **PDF:** QuestPDF (check its license terms) or a lightweight HTML-to-PDF option.
- **Images:** compress on the device before saving (max 1600 px on the long edge, JPEG quality around 75).
- **Logging:** Serilog on the API; local log file on the device for sync errors.

---

## 4. Multi-company, multi-user and permissions

### 4.1 Entities

- `AppUser` (ASP.NET Core Identity, global, in `AuthDbContext`): Id, Email, DisplayName, password hash, lockout. Identity roles/claims are **not** used for company access.
- `Company`: Id, Name, Address, Phone, PAN/VAT no., FiscalYearStartMonth, Calendar (BS/AD), Currency (NPR), **TimeZoneId** (default `Asia/Kathmandu`, used for "today")
- `CompanyMember` (server-only, D11): CompanyId, UserId, DisplayName, Role, CustomPermissions (text[]; null = role template), Status (Active/Revoked). Exactly one Owner per company (partial unique index).
- `Invitation` (server-only): CompanyId, normalized email, DisplayName, Role, CustomPermissions, **SHA-256 of the token** (the token itself is shown once), 7-day expiry, single use. In v1 the inviter passes the token on; email delivery comes later.
- Implemented in `LekhaBook.Domain/Permissions.cs`, `Membership.cs`, `AccessRules.cs` (EntryRules, StaffRules, CompanyClock).

### 4.2 Roles and permissions

Use a **per-module permission set**, the same model as Karobar ([discovery 06](discovery/06-staff-permissions.md)). Each role is a **template**; an Owner or Partner can customize any member's permissions ("Edit Permission").

**Modules × actions (v1):**

| Module | Actions |
|---|---|
| Parties: customers | Create, View, Edit, Delete |
| Parties: suppliers | Create, View, Edit, Delete |
| Payment In | Create, View, Edit, Delete |
| Payment Out | Create, View, Edit, Delete |
| Income & Expenses | Create, View, Edit, Delete |
| Money accounts | View, Add, Edit, Delete account; Create adjustment / transfer; Edit/Delete adjustment |
| Party adjustments | Manage |
| Reports | View party, transaction, income/expense and business-status reports; view dashboard stats; export/share |
| Staff | Create, View, Edit, Delete |
| Business settings | Edit business profile; edit party/transaction settings (incl. prefixes, serial reset); delete business |
| Restrictions | **Lock Transaction Date** (can only enter today's date); same-day-only edits |

**Role templates (defaults):**

| Role | Default |
|---|---|
| **Owner** | Everything, including delete business and transferring ownership. Exactly one per company |
| **Partner** | Everything except delete business |
| **Manager** | Create, view and edit everywhere; **no deletes**; no staff or settings; can't add/edit/delete accounts |
| **Accountant** | Everything except staff management, business edit/delete and delete account |
| **Entry Person** | **Create + View** parties, payments, income/expense and adjustments; **sees party and account balances** in pickers and ledgers (needed while taking payments); edits/deletes only **own** entries, **same day only**; no reports export, staff or settings; Lock Transaction Date **on** |

Differences from Karobar, on purpose:
- Their Entry Person can edit transaction settings, including resetting serial numbers. Ours can't.
- Their Entry Person can't see any balance. Ours sees balances, because a phone user taking a payment needs the party's due amount.
- Sales Person and Stock Manager are dropped while sales and inventory are out of scope.

Server rules:
- **Every member's device pulls the full company data.** Locally computed balances must be correct, so sync is never filtered by role. Role restrictions on *viewing* are enforced in the UI and on the API's report endpoints. Restrictions on *writing* are always enforced on the server during push.
- "Same day only" and "Lock Transaction Date" are judged by the server's `CreatedAtUtc`, not the device clock.
- A user can belong to many companies. After login, they pick a company from a switcher (in the sidebar on desktop, the app bar on phones).
- **Members with Staff → Create permission (Owner and Partner by default) can add users:** they invite by phone or email; the invitee accepts after logging in, and the status goes from Invited to Accepted.
- **The server checks every permission.** The client only hides buttons.
- **Revocation:** when a user is removed, the server rejects their sync for that company (403). The client then **wipes that company's local data** on its next connect.

---

## 5. Accounting domain (simple, correct and sync-safe)

### 5.1 Accounts

One `Account` table with `AccountType` = `Party` | `Cash` | `Bank` | `Cheque` | `Wallet` | `Income` | `Expense`.

**Money accounts** are `Cash`, `Bank`, `Cheque` and `Wallet`. They are what forms call **"Payment Method"**. Seed every new company with **Cash**, **Cheque** and **Online Wallet**; bank accounts are added by the user. Petty cash is just another `Cash` account. (Source: [discovery 03](discovery/03-accounts.md).)

- **Income / Expense heads:** seeded per company (Sales/Goods, Other Income, Purchase/Goods, Petty Expenses, Rent, Salary, Transport…). Users can add more heads. These are what petty-cash entries and credit sales/purchases post against.
- **Party:** FullName (required), Phone, PartyCategoryId, PartyType (Customer/Supplier), PhotoAttachmentId, Address, Email, PAN, OpeningBalance, OpeningSide (**To Receive / To Give**), OpeningAsOfDate
- **Bank / Wallet:** Name (bank or wallet name), AccountHolderName, AccountNumber (masked in lists), OpeningBalance ("Current Account Balance")
- **Cash / Cheque:** Name ("Cash", "Petty Cash", "Cheque"), OpeningBalance. Cheque entries may carry an optional Cheque No. and Cheque Date (an improvement over Karobar, [gap G4](discovery/gap-notes.md))
- **Income and expense categories are accounts** of type `Income` / `Expense` (decision D7). Seeded expense heads: General, Health, Recharge, Food, Entertainment, Fuel, Shopping, Travel. Seeded income heads: Salary, Investment, Commission. The "Expense Category" report is simply balances by Expense account.
- `PartyCategory` is a plain user-managed lookup (not an accounting account).
- **Opening balances live on the account** (`OpeningBalance`, `OpeningSide`, `OpeningAsOf`); there is no opening transaction (decision D8). Opening equity is implicit: −Σ signed openings, so all balances plus it sum to zero.
- Seeding is done by `CompanySeeder` in the domain (not EF `HasData`): Cash, Cheque, Online Wallet, the system `Adjustment` account, and the category heads. System accounts are flagged `IsSystem` and can't be deleted.

### 5.2 Transactions

`Transaction`: Id (UUIDv7), CompanyId, VoucherNo, IsManualNo, Date, Type, Amount, FromAccountId, ToAccountId, Remarks, ChequeNo?, ChequeDate?, CreatedBy, CreatedOnDevice, Status (Posted/Void), plus the sync fields in [§ 6.1](#61-sync-columns-on-every-syncable-table).

Types:

| Type | Meaning | Debit | Credit |
|---|---|---|---|
| `PaymentIn` | Got money from a party | Money account | Party |
| `PaymentOut` | Gave money to a party | Party | Money account |
| `PartyAdjustAdd` | Party Adjust Balance → **Add Balance** (more receivable from the party) | Party | Adjustment (system) |
| `PartyAdjustReduce` | Party Adjust Balance → **Reduce Balance** | Adjustment (system) | Party |
| `Expense` | Expense paid (petty cash bills, fuel…) | Expense category | Money account |
| `OtherIncome` | Income received without a party | Money account | Income category |
| `AddMoney` | Account Adjust → **Add Money** | Money account | Adjustment (system) |
| `ReduceMoney` | Account Adjust → **Reduce Money** | Adjustment (system) | Money account |
| `TransferMoney` | Move money between accounts (cash → bank deposit, bank → cash withdrawal, bank → wallet…) | To account | From account |

Type names and labels match what Karobar users already use ([discovery workflows](discovery/workflows.md)). Credit sales and purchases (invoices) are out of v1 scope. The `Account` model already allows adding them later.

A party or category is identified by the From/To account itself, so transactions have no separate `PartyId` or `CategoryId` columns. `Adjustment` is a seeded system account per company. The rules are implemented and tested in `LekhaBook.Domain/PostingRules.cs` and `Balances.cs`.

Store each transaction as **one row with From and To accounts**. That gives you double entry without a journal-lines table, which keeps sync and conflicts simple. Exact labels and screens follow the Phase 1 findings.

`Attachment`: Id, CompanyId, TransactionId, FileName, ContentType, SizeBytes, LocalPath (client only), ServerKey, UploadStatus, sync fields.

`AuditLog`: who changed what, with before/after JSON, on every edit or void of a transaction.

### 5.3 Balances

- **Never store a running balance.** Compute it with `SUM` over transactions (indexed on `CompanyId, AccountId, Date`).
- Ledger screens compute the running balance in the query, for example with a window function on Postgres or in memory for one account's page on SQLite.
- Optionally cache per-day snapshots locally, rebuilt after each sync. Never sync them.

### 5.4 Voucher numbers offline

- On the device, give a **provisional** number such as `TMP-<DeviceShort>-0007`.
- On sync, the server assigns the final per-company, per-fiscal-year sequence (for example `RC-2082-0153`) and returns it. The client updates the record.
- The UI shows "Pending" next to provisional numbers.
- Like Karobar, every number field has a **Manual ↔ Auto-Generate** toggle. A manual number is kept as typed; the server only checks it is unique for that type and fiscal year, and a clash is reported as a conflict.
- **Per-type prefixes** (Payment In, Payment Out, Expense, Income…) and "Reset serial number" live in Settings → Transactions, and only Accountant and above can change them.

### 5.5 Dates

- Store dates as `DateOnly` in AD; store timestamps in UTC.
- If discovery confirms BS dates ([D2](#10-decisions-recommended-defaults)), display and enter dates in BS through a BS↔AD conversion service with a tested lookup table, and use a BS date picker component. Fiscal year follows the company setting (Nepal: Shrawan 1).

---

## 6. Offline-first sync design

### 6.1 Sync columns on every syncable table

| Column | Purpose |
|---|---|
| `Id` (UUIDv7, client-generated) | No ID clashes across devices |
| `CompanyId` | Tenant scope |
| `Version` (bigint, server-assigned, monotonic per company, **in commit order**, see [§ 6.2](#62-flow)) | Change tracking for pull |
| `CreatedAtUtc` (server time) | Time rules such as "same-day edit" use this, never the device clock |
| `UpdatedAtUtc`, `UpdatedBy`, `UpdatedOnDevice` | Audit and tie-breaks |
| `IsDeleted` (soft delete) | Deletes sync like edits |
| `BaseVersion` (client only) | The server version this local edit started from, used to detect conflicts |

### 6.2 Flow

1. **Every local write** goes into the SQLite table **and** into an `Outbox` row (entity, id, operation, payload, attempt count), in one local transaction.
2. **Push:** `POST /api/sync/{companyId}/push` with a batch of outbox items and an idempotency key per item. The server applies each in a DB transaction, checks permissions, assigns a `Version`, and returns results per item (ok, final voucher no., conflict, rejected).
   - **Versions must follow commit order, or pull skips rows.** Take the version from a per-company counter row (`CompanySyncState`) with `SELECT … FOR UPDATE` *inside* the push transaction. That serializes writes per company, so a version can never become visible before a lower one. Don't use a plain sequence or identity column for this.
3. **Pull:** `GET /api/sync/{companyId}/pull?since={lastVersion}&take=500` returns changed rows (including deletes). The client upserts them and stores `lastVersion`. Repeat until done.
4. **Triggers:** app start, regaining connectivity, after each save (debounced 5 s), every 5 minutes in the foreground, and pull-to-refresh. Use a background task where the platform allows it.
5. **Order:** push, then pull. Master data (accounts) before transactions. Attachment metadata syncs with rows; attachment **files** upload separately (see [§ 7](#7-attachments-camera--gallery--file)).

### 6.3 Conflict rules

| Data | Rule |
|---|---|
| Master data (party name, phone, bank info) | Last writer wins on the server; the losing value goes to `AuditLog` |
| **Opening balance fields** on an account (amount, side, as-of date) | Treated as money: a concurrent change goes to "Needs review", like a posted transaction (D8) |
| **Posted transactions** (amount, date, accounts) | If `BaseVersion` ≠ the current server `Version`, **don't overwrite**. Mark it **Conflict**, keep both versions, and show it in a "Needs review" list for Accountant and above |
| Delete vs edit | The delete wins only if the edit is older. Otherwise flag it as a conflict |
| Revoked user or company | Reject with 403; the client wipes local data for that company |

### 6.4 Device safety

- Keep a local database **per user** (file `lekhabook_<userId>.db`), scoped by `CompanyId` inside.
- **Offline login:** after one online login, allow offline unlock with a PIN or biometric. Keep the refresh token in `SecureStorage`.
- **Logout:** if the outbox has unsynced items, warn the user and offer "Sync now" or "Log out anyway (data stays on device)".
- Show a status chip in the app bar: **Online · Synced** / **Offline · 3 pending** / **Sync error**. Tapping it shows details.

---

## 7. Attachments (camera / gallery / file)

- On every transaction form there is a paperclip button with **Take photo**, **Choose from gallery** and **Choose file (PDF)**. Allow several per transaction (default limit 5).
- **MAUI:** use `MediaPicker.CapturePhotoAsync` / `PickPhotoAsync` and `FilePicker`. Compress, save under `FileSystem.AppDataDirectory/attachments/<companyId>/`, and create an `Attachment` row with `UploadStatus=Pending`.
- **Browser:** `<InputFile>` with `capture="environment"` opens the phone camera in mobile browsers. Upload immediately.
- **Upload queue:** `POST /api/attachments` (multipart) with retry and backoff, only after the parent transaction is synced. Optionally upload only on Wi-Fi (a setting).
- **Server:** check type (jpeg/png/webp/pdf) and size (max 5 MB after compression), store in object storage (S3-compatible or Azure Blob) or a local folder in dev, and serve through short-lived signed URLs with permission checks.
- **Permissions:** add camera and photo usage descriptions to `Info.plist` (iOS) and declare camera and media permissions in the Android manifest.
- Thumbnails show in the transaction list; tapping one opens a full-screen viewer with zoom.

---

## 8. UI / UX requirements

### 8.1 Responsive, mobile first

- **Phone (<600 px):** bottom navigation (Home, Parties, Cash & Bank, Reports, More), a floating "+" button opening a quick-add sheet (Payment In / Payment Out / Expense / Income / Transfer Money), full-screen forms, lists as cards.
- **Tablet and desktop (≥960 px):** left sidebar, data grids with sorting and filtering, forms in a side panel or dialog, a ledger with a running-balance column.
- Test on 360×640, 390×844, 768×1024 and 1440×900.

### 8.2 Touch mode and keyboard mode

- **Auto-detect** with `matchMedia('(pointer: coarse)')`, with a manual override in Settings ("Touch mode / Desktop mode") stored per device.
- **Touch mode:** targets at least 48 px, larger fonts, swipe actions on list items (edit / attachment / delete with confirmation), pull-to-refresh.
- **Desktop mode:** dense rows and keyboard shortcuts.

### 8.3 Enter-key navigation (like a Windows app)

Build a JS module `wwwroot/js/enterNav.js` in `LekhaBook.UI` and attach it to every form with a `<EnterNavForm>` wrapper component:

- **Enter** moves focus to the next focusable, enabled, visible field in DOM order.
- **Shift+Enter** moves to the previous field.
- **Enter on the last field, or Ctrl+S,** saves. **Esc** cancels or closes.
- **Textarea:** Enter inserts a new line; Ctrl+Enter moves on.
- **Dropdown/autocomplete:** when its list is open, Enter selects; when it is closed, Enter moves on.
- **Amount fields** select all their text on focus.
- **Mobile soft keyboards:** set `enterkeyhint="next"` on every field except the last, which gets `enterkeyhint="done"`. Set `inputmode="decimal"` on amounts and `inputmode="tel"` on phone numbers. This makes the Android and iOS keyboards show **Next**, which goes through the same handler.
- **Global shortcuts (desktop):** use the same keys as Karobar so switching users feel at home ([discovery 07](discovery/07-settings-and-ui.md)):
  - **Add data:** `Alt+I` Payment In, `Alt+O` Payment Out, `Alt+E` Expense, `Alt+N` new Party, `Alt+T` Transfer Money, `Alt+A` Add/Reduce Money. Other Income has no Karobar key; pick a free one.
  - **Go to:** `Shift+D` Dashboard, `Shift+P` Parties, `Shift+E` Expense, `Shift+R` Reports, `Shift+S` Settings, `Shift+M` toggle sidebar.
  - `/` focuses search. `Shift+K` (or `?`) opens the shortcuts dialog.
  - Shift-shortcuts must not fire while focus is in a text field.
- The same JS runs in the browser and inside the MAUI BlazorWebView. Test both.

### 8.4 Forms (the quick-entry flow)

Field order on a Payment In / Payment Out form (same as Karobar, [discovery 02](discovery/02-payments.md)): **Receipt No.** (auto, with a Manual toggle) → **Date** (today) → **Party** (autocomplete with "+ Add new" inline; the party's current balance shows beside the label) → **Amount** → **Payment Method** (money accounts listed **by name with balance**) → **Remarks** → **Attachments** → **Save**.
- When the party is already chosen (opened from its ledger), focus starts on **Amount**.
- **Save & New** keeps the date and payment method.
- Expense and Income forms use the same order, with **Category** in place of Party.

### 8.5 Localization and formatting

- `IStringLocalizer` resources for English and Nepali (`.resx`). Language switch in Settings.
- Amounts use NPR with Indian/Nepali digit grouping (`1,23,456.00`), and optionally Devanagari digits.
- Dates in BS or AD per the company setting (see [§ 5.5](#55-dates)).

### 8.6 Accessibility and polish

Labels on every input, visible focus rings, color contrast AA, empty states with a clear next action, skeleton loaders, light/dark theme.

---

## 9. API (server)

- **Auth** (ASP.NET Core Identity API endpoints, D5/D10): `POST /api/auth/register`, `/api/auth/login` (returns bearer `accessToken` + `refreshToken`), `/api/auth/refresh`, `/api/auth/manage/info`, … Rate-limited per IP (`RateLimits:AuthPerMinute`, default 10).
- **Me:** `GET /api/me` (user + companies with role and effective permissions), `PUT /api/me` (display name)
- **Companies:** `POST /api/companies` (creates, seeds default accounts, caller becomes Owner), `GET /api/companies/{companyId}`
- **Staff:** `GET /api/companies/{companyId}/members`, `PUT …/members/{memberId}` (role/permissions), `DELETE …/members/{memberId}` (revoke)
- **Invitations:** `GET/POST /api/companies/{companyId}/invitations`, `DELETE …/invitations/{invitationId}`, `POST /api/invitations/accept` (signed in with the invited email)
- **Masters** (phase 5, D14): `GET/POST /api/companies/{companyId}/parties`, `GET/PUT/DELETE …/parties/{id}`, `GET …/parties/{id}/ledger`; `GET/POST …/party-categories`; `GET/POST …/accounts` (money accounts only), `GET/PUT/DELETE …/accounts/{id}`, `GET …/accounts/{id}/ledger`. Creates take a client-generated `id`; repeating one returns the saved row
- Every `{companyId}` route requires active membership (+ permission) as an **authorization policy**, so non-members get 403 before the body is read. A test walks all `{companyId}` endpoints to keep this true.
- `POST /api/sync/{companyId}/push`, `GET /api/sync/{companyId}/pull`
- `POST /api/attachments`, `GET /api/attachments/{id}` (signed URL)
- `GET /api/companies/{id}/reports/{party-statement|cash-book|bank-book|day-book|summary}` (used by the browser and for server-side PDFs)
- Cross-cutting: tenant filter middleware, permission policies, rate limiting, ProblemDetails errors, health checks, OpenAPI/Swagger UI in Development.
- PostgreSQL: one database, with `CompanyId` on every tenant table and composite indexes `(CompanyId, Version)` and `(CompanyId, AccountId, Date)`. EF global query filters for `CompanyId` and `IsDeleted`.

---

## 10. Decisions (recommended defaults)

Use the default unless the user says otherwise. Record each final choice in `docs/DECISIONS.md`.

| # | Decision | Recommended default | Alternatives |
|---|---|---|---|
| D1 | Browser offline | ✅ **Decided: the browser is online-only in v1** (Blazor Web App calling the API). Phones and Windows use the MAUI app for offline | Blazor WASM PWA with SQLite-in-browser (OPFS) or IndexedDB, plus the same sync engine (phase 2) |
| D2 | Date system | ✅ **Decided: store AD; display BS by default, AD selectable per company** (confirmed in discovery) | AD only |
| D3 | UI library | **MudBlazor** (free, MIT, responsive) | DevExpress Blazor (needs a license), Radzen, Fluent UI Blazor |
| D4 | Server hosting | **Docker on a VPS** (API + PostgreSQL + Caddy for TLS). The API **and the web host** must persist ASP.NET data-protection keys (or bearer tokens, the web auth cookie and its encrypted state break on restart) and use `UseForwardedHeaders` with the proxy's address (or the sign-in rate limit sees one IP) | Azure App Service + Azure Database for PostgreSQL; AWS |
| D5 | Login method | ✅ **Decided: email + password (ASP.NET Core Identity), optional PIN/biometric unlock on the device later.** Phone OTP / Google sign-in in a later version | Phone OTP through an SMS gateway (costs per SMS), Google sign-in |
| D6 | Attachment storage | **S3-compatible object storage** (MinIO in dev) | Postgres `bytea` (simple, but the DB grows fast) |

---

## 11. Build phases and exit criteria

| Phase | Work | Exit criteria |
|---|---|---|
| **1. Discovery** ✅ | [§ 1](#phase-1-discover-the-reference-app-with-microsoft-edge-read-only) | User approves `docs/discovery/00-summary.md` and the v1 scope. **Done 2026-11-23.** Items not covered are listed in the summary's checklist; check them in a test business later |
| **2. Skeleton** 🟡 | Solution and projects from [§ 3.1](#31-projects-one-lekhabooksln), `global.json`, central packages, docker-compose Postgres, `.vscode` launch/tasks, README run steps, CI workflow (build + test) | `dotnet build` passes; the API, Web and Windows app all launch from VS **and** VS Code; the Android emulator shows the shared home page. **Status 2026-11-23:** solution builds (0 warnings), `/health` test passes, Web renders at phone + desktop sizes, Windows app opens on the dashboard, **Android emulator (API 36, `LekhaBook_Pixel`) shows the shared home page** with correct system-bar insets, and the sync chip switches to Offline when the network is cut. **Still open:** launching from VS/VS Code by hand, first CI run, docker-compose (Docker not installed on the dev PC) |
| **3. Domain and data** ✅ | Entities, EF configurations, Postgres migrations, SQLite `EnsureCreated`/migrations, seed data | Domain tests for the debit/credit and balance rules pass; migrations apply to a fresh Postgres. **Done 2026-11-24:** 44 domain tests pass; migrations apply to a fresh PostgreSQL 18.3 and to SQLite, and SQL balances equal the domain calculator on both (`LekhaBook.Data.Tests`, 4/4); the app creates its device database on the Android emulator; Android Release build installs and starts. CI runs the PostgreSQL test against a service container. The developer's persistent dev database (README "Local database") is still to be set up before phase 4 needs it |
| **4a. Auth and tenancy (server)** ✅ | Identity, bearer/refresh tokens, companies, invites, roles, permission policies | Integration tests: a user without membership gets 403 (every `{companyId}` route); revoked member gets 403; a customized permission overrides the role template; no permission escalation (grants or managing someone with more access); invites are single-use tokens checked against the (unconfirmed) invited email; token refresh isn't rate-limited. Entry rules (Entry Person: own entries, same day; Lock Transaction Date) are **unit-tested** in `AccessRulesTests`. **Done 2026-11-24** (70 domain + 4 data + 11 API tests) |
| **4b. Auth and tenancy (UI)** ✅ | Sign-in / sign-up screens, token storage (MAUI `SecureStorage`; web: HttpOnly cookie + per-circuit tokens, D13), create company, company switcher, staff + invite screens, **one device database per user** (`lekhabook_<userId>.db`) | Sign in on web and Android; create a company; switch companies; invite + accept; app refuses a revoked company. **Done 2026-11-24** against the throwaway test database: web sign-up → create company → invite → second user signs up via the invite link (return URL kept through the cookie redirect) → joins as Entry Person (no Staff access) → switches companies (choice survives reload) → owner revokes → company disappears (web, after reload). Android: sign-in via 10.0.2.2, per-user DB file created, stays signed in after restart and after an **offline** restart (cached profile + offline banner). Windows: app starts on the sign-in screen (SecureStorage read OK). 9 session/DI tests, incl. offline start with an expired token. **Not verified:** Windows sign-in / token save; revocation on Android; iOS; `Asia/Kathmandu` resolution on Android (no app code path uses it yet) |
| *(moved)* | **Integration** checks for the entry rules (Entry Person can't edit others' entries or after the same day; Lock Transaction Date rejects back-dated entries) | Land with the first transaction write endpoint (phase 6 web CRUD / phase 8 sync push), using `EntryRules` |
| **5. Masters UI** ✅ | Party, Bank and Cash account list and form pages (responsive, enter-nav) | Works in the browser and the Android emulator; the keyboard-only flow completes a party creation. **Done 2026-11-24** (D14: both hosts use the API until phase 8). API: parties, party categories, money accounts and their ledgers, with per-type party permissions, company-scoped lookups, idempotent creates (client ids), server versions on every write (seeded accounts included) and audit rows. UI: Parties list (search, type/balance filters, sort, To Receive/To Give totals) → detail with ledger → add/edit; Cash & Bank list (total balance, masked account numbers) → detail → add/edit (Bank, Wallet, **Cash/petty cash**); dashboard tiles show real totals; `EnterNavForm` + `enterNav.js` (Enter/Shift+Enter, Enter-on-last or Ctrl+S saves, Esc cancels, radio groups as one stop, `enterkeyhint` next/done, `inputmode` decimal/tel, amounts select on focus, first invalid field focused after a failed save). Verified: keyboard-only party and account creation in the browser (desktop), phone layout at 375 px, Android soft keyboard **Next** walks the form and saves, offline shows a "needs a connection" message. 124 tests (78 domain, 21 application, 4 data, 21 API). **Deferred:** BS dates (phase 10; AD picker for now), party photo, party import, category management screen (categories are added inline from the party form), desktop master–detail layout and global Alt/Shift shortcuts (phase 10) |
| **6. Transactions UI** | Quick-add sheet, all transaction types, Save & New, edit/void with audit | Ledger balances match a hand-calculated test sheet (add to tests) |
| **7. Attachments** | Camera, gallery and file on MAUI and browser; compression; upload queue; viewer | Photo taken on Android offline appears on the web after reconnect |
| **8. Sync engine** | Outbox, push/pull, provisional voucher numbers, conflicts, revocation wipe, status chip | `LekhaBook.Sync.Tests`: 2 devices offline edit the same party and the same transaction, then reconnect, with the expected results from [§ 6.3](#63-conflict-rules); no duplicates after retrying a push; **concurrent pushes from 2 devices to one company, with a pull in between, lose no rows** |
| **9. Reports** | Party statement, cash book, bank book, day book, dashboard; PDF export and share | Report totals equal the domain balance functions in tests |
| **10. Polish** | Nepali localization, BS dates, touch/desktop mode toggle, dark mode, empty states, shortcuts help | Manual checklist at 4 viewport sizes; Lighthouse accessibility ≥ 90 on Web. Re-check fast Enter-key entry on a deployed or throttled web host (50–300 ms round trips): phase 5 fixed a MudAutocomplete focus-steal race (`SelectOnActivation`/`OpenOnFocus` off) at localhost latency only |
| **11. Release** | Android signed AAB, iOS (needs a Mac with Xcode and an Apple developer account), Windows MSIX, API deployment per D4 | The user installs and uses it on a real Android phone and an iPhone with airplane-mode testing |

---

## 12. Opening the project in Visual Studio and VS Code

**Visual Studio 2026 (v18), with the ".NET MAUI" and "ASP.NET and web development" workloads:**
- Open `LekhaBook.sln`. Use **multiple startup projects**: `LekhaBook.Api` + `LekhaBook.Web`, or `LekhaBook.Api` + `LekhaBook.App` (Android emulator or Windows Machine).

**VS Code:**
- Extensions (put them in `.vscode/extensions.json`): C# Dev Kit, .NET MAUI, and optionally Docker and PostgreSQL.
- `.vscode/launch.json` contains **API**, **Web** and a compound **API + Web** configuration. Run the MAUI app with the .NET MAUI extension's target picker.
- `.vscode/tasks.json` has: `db:up` (`docker compose up -d`), `build`, `test`, `ef:migrate`.

**Command line:**

```powershell
docker compose up -d
dotnet run --project src/LekhaBook.Api
dotnet run --project src/LekhaBook.Web
dotnet build src/LekhaBook.App -t:Run -f net10.0-android
```

**Prerequisites in the README:** .NET 10 SDK, `dotnet workload install maui`, Android SDK/emulator, Docker Desktop; for iOS, a Mac with Xcode (pair it from Visual Studio, or build on the Mac).

---

## 13. Testing strategy

- **Unit:** domain rules, BS↔AD conversion, voucher numbering, permission matrix.
- **Integration:** API with Testcontainers PostgreSQL: auth, tenancy isolation, sync push/pull idempotency.
- **Sync simulation:** in-memory SQLite "devices" against a test server; scripted offline/online sequences.
- **UI:** bUnit for components (enter-nav wrapper, forms); Playwright (Edge) end-to-end against `LekhaBook.Web` at phone and desktop viewports.
- **Manual device checklist:** airplane mode → add 3 entries with photos → kill the app → reopen → go online → verify on the web.

---

## 14. Definition of done (v1)

- Every in-scope workflow from `docs/discovery/workflows.md` works on Android, iOS, Windows (offline + online) and in the browser (online).
- No data loss or duplicates in the sync test suite; conflicts surface for review.
- Balances are always computed, never stored, and match the reports.
- Multi-company and multi-user with server-enforced permissions; revocation wipes local data.
- Enter-key navigation and shortcuts work on desktop; Next/Done works on phone keyboards; touch mode is usable one-handed.
- The project opens and runs from Visual Studio and VS Code using the README alone.
