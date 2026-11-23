# Discovery summary: Karobar web (my.karobarapp.com)

- **Reviewed on:** 2026-11-23, in Microsoft Edge 158 (separate profile, driven with Playwright over CDP)
- **Viewports:** 1440×900 desktop and 390×844 phone
- **Account:** the user's **live business**. The review was **read-only**: forms were opened but never saved, and nothing was deleted or sent.
- **Personal data:** no party names, phone numbers, staff names or screenshots of live data are stored in this repo. Screenshots stayed in a temporary folder outside the repo.

## 1. Menu tree (left sidebar on desktop; hamburger drawer on phone)

```
[Business switcher ▾]            (combobox at top of sidebar)
Business
  Dashboard                       /
  Parties                         /parties
  Inventory                       /inventory                 (out of scope)
  Sales ▸                         /sales-invoices            (out of scope)
  Purchase ▸                      /purchase                  (out of scope)
  Expense                         /expense
  Other Income                    /income
  Manage Accounts                 /manage-account
Management
  Reports                         /reports
  Manage Staffs                   /manage-staffs
  Import Data ▸                   /import-data/parties
  Business Tools ▸                /business-tools/business-cards   (out of scope)
  Refer & Win                     /refer-and-win              (not doing)
Others
  Help & Support / Tutorials / What's New
  Settings                        /settings/general-settings
  Theme Switch
Header: language (English / नेपाली) · keyboard shortcuts · notifications · theme · user menu
```

**Dashboard:**
- Quick buttons: **Quick POS**, **Add Sales**, **Add Purchase** and **Add More**.
- Tiles: **To Receive**, **To Give**, and Sales, Purchase and Expense for the current BS month.
- A 7-day **Cashflow** chart showing Money In and Money Out.
- **Recent Transactions:** Date, Type, Name, Total, Rec/Paid, Balance.

## 2. Screen files

| File | Covers |
|---|---|
| [01-parties.md](01-parties.md) | Party list, filters, Add/Edit Party, party ledger, party Adjust Balance |
| [02-payments.md](02-payments.md) | Payment In, Payment Out, numbering, payment method, attachment |
| [03-accounts.md](03-accounts.md) | Manage Accounts (Cash/Bank/Cheque/Wallet), Add Account, Add/Reduce/Transfer Money |
| [04-expense-income.md](04-expense-income.md) | Expense and Other Income forms, categories |
| [05-reports.md](05-reports.md) | Report catalogue; party statement, cash-in-hand, bank statement, day book |
| [06-staff-permissions.md](06-staff-permissions.md) | Multi-user staff, roles, the full permission matrix |
| [07-settings-and-ui.md](07-settings-and-ui.md) | Settings, localization (BS/AD, Nepali), shortcuts, mobile web behavior |
| [workflows.md](workflows.md) | End-to-end flows to reproduce |
| [gap-notes.md](gap-notes.md) | What we will do better |

## 3. Key findings

1. **"Payment method" means a money account.** Every business has the accounts **Cash**, **Bank Account(s)**, **Cheque** and **Online Wallet(s)**. Payment In/Out, Expense and Income post to one of them. Cheque is a holding account, not cheque-detail fields.
2. **Party balance is "To Receive" or "To Give".** The opening balance has a side and an "As of" date. Parties can also be adjusted directly (**Adjust Balance**: Add or Reduce, with a live preview of the adjusted balance).
3. **Account money movements:** **Add Money**, **Reduce Money** and **Transfer Money** (from one account to another), each with an optional image.
4. **Voucher numbers:** auto-generated per type (for example Payment In #1061), with a **Manual / Auto-Generate** toggle and optional **prefixes** (Settings → Transactions). There is also a "Reset Transaction Serial Number" setting.
5. **Attachments:** a single camera-icon tile on payment, expense, income and money forms. On the web it accepts **png/jpg/jpeg only** (no PDF), and thumbnails show in ledger rows.
6. **Dates are BS by default:** displayed like `2083 Man 07` (Mangsir), with a BS date picker covering 2000–2090. Settings can switch the calendar AD/BS. The number format uses lakh grouping (`10,00,000`), and the currency is `Rs.`
7. **Multi-business and multi-user:**
   - A business switcher sits in the sidebar.
   - Staff roles are Partner, Manager, Accountant, Sales Person, Stock Manager and Entry Person.
   - There is a granular create/view/edit/delete permission matrix per module, editable per staff member.
8. **Bilingual data:** party names commonly mix Latin and Devanagari text, so search must handle both.
9. **Keyboard:**
   - Global Alt-shortcuts open entry forms (for example Alt+I for Payment In, Alt+O for Payment Out, Alt+E for Expense, Alt+N for Party).
   - Shift-shortcuts navigate (for example Shift+P for Parties).
   - Esc closes dialogs.
   - Enter-to-next-field was **not tested**, because pressing Enter could save a real entry. Test it on our own app instead.
10. **Mobile web is weak:** at 390 px, the parties master-detail layout overflows (horizontal scroll), there is no bottom navigation, and the header crowds. Their phone experience is the native app, which we did not review.

## 4. Proposed v1 scope (for approval)

> ✅ **Approved by the user on 2026-11-23.** The changes in § 5 have been applied to AGENT_PLAN.md.

| Module | Karobar has | LekhaBook v1 |
|---|---|---|
| Parties (customer/supplier, opening balance, category, address, email, PAN, photo) | ✔ | **In** |
| Party ledger with running balance, search, sort, filters | ✔ | **In** |
| Payment In / Payment Out with attachment | ✔ | **In** |
| Party Adjust Balance (add/reduce) | ✔ | **In** |
| Money accounts: Cash, Bank, Cheque, Wallet; Add/Reduce/Transfer Money | ✔ | **In** (Petty Cash = a Cash-type account) |
| Expense and Other Income with categories (simple total; item lines later) | ✔ | **In** |
| Reports: Party Statement, All Party, Cash in Hand, Bank Statement, Day Book, All Transactions, Income/Expense category; PDF + Excel | ✔ | **In** |
| Multi-business, staff, roles, permission matrix | ✔ | **In** |
| Settings: calendar BS/AD, language, number format, prefixes, categories, app lock | ✔ | **In** |
| Import parties (Excel) | ✔ | Later |
| Send reminder (WhatsApp/SMS) | ✔ | Later |
| Sales/Purchase invoices, returns, quotation, Quick POS | ✔ | Not in v1 |
| Inventory, items, stock adjustments, item reports | ✔ | Not in v1 |
| Tax/VAT reports, discount report, profit & loss | ✔ | Not in v1 |
| Business cards, greeting cards, notebook, Refer & Win | ✔ | Not doing |

## 5. Proposed changes to AGENT_PLAN.md (after you approve)

- **§ 5.1:** model **Cheque** and **Online Wallet** as account types next to Bank and Cash. Add party **Category** and **Photo**.
- **§ 5.2:** rename types to match users' words: Payment In, Payment Out, Add Money, Reduce Money, Transfer Money, Expense, Other Income, and Party Adjustment (Add/Reduce).
- **§ 4.2:** replace our 5 roles with **Partner, Manager, Accountant, Entry Person** (dropping Sales Person and Stock Manager while sales and inventory are out of scope) and adopt the **per-module Create/View/Edit/Delete matrix** from [06-staff-permissions.md](06-staff-permissions.md).
- **§ 5.4:** keep the Manual/Auto numbering toggle and per-type prefixes. Offline numbers stay provisional until synced.
- **§ 7:** allow PDF as well as images (Karobar allows images only).
- **§ 8.3:** reuse familiar shortcuts (Alt+I, Alt+O, Alt+E, Alt+N; Shift+P etc.) so switching users feel at home.
- **D2** is confirmed: **BS is the default display calendar**, with AD selectable.

## 6. Discovery checklist (AGENT_PLAN § 1.4)

| Item | Status | Where / note |
|---|---|---|
| Full menu tree, both viewports | ✓ | § 1; [07](07-settings-and-ui.md) mobile web |
| Business/company: create, switch, edit profile, fiscal year | Partial | Profile ✓ ([07](07-settings-and-ui.md)). Switcher options not read. Create business not seen. Fiscal year seen only as the "This Fiscal Year" date filter |
| Staff/multi-user: invite flow, roles, permission matrix | Partial | Roles and matrix ✓ ([06](06-staff-permissions.md)). How the invite reaches the staff member is not visible on the web |
| Parties: types, fields, opening balance, ledger, statement, reminders | ✓ | [01](01-parties.md). Reminder button seen, not used |
| Transactions: receipt/payment, cash in/out, expense, transfers | ✓ | [02](02-payments.md), [03](03-accounts.md), [04](04-expense-income.md) |
| Bank accounts: add, opening balance, deposit/withdraw, transfer, cheque fields | ✓ | [03](03-accounts.md). No cheque-detail fields exist |
| Cash / petty cash | ✓ | Cash account + Add/Reduce/Transfer Money ([03](03-accounts.md)) |
| Attachments: where, camera vs gallery, preview, delete | Partial | Where and accepted types ✓. On the web it is one image file input (no separate camera vs gallery). Preview/delete not checked |
| Reports: party statement, day book, cash book, bank book, summary, export, share | Partial | Catalogue and layouts ✓ ([05](05-reports.md)). Export = Print PDF + Excel. **Share** not seen on the web |
| Search, filters, date ranges, sorting | ✓ | [01](01-parties.md), [03](03-accounts.md) |
| Localization: language, BS/AD, NPR lakh format | ✓ | [07](07-settings-and-ui.md) |
| Offline behavior of the web version | **Not tested** | Would mean cutting the network during a live session |
| Keyboard: Tab/Enter, shortcuts | Partial | Shortcuts ✓. Enter key not tested |
| Out-of-scope modules listed | ✓ | § 4 |

## 7. Not covered (and why)

- **Offline behavior of the web app:** not tested.

- **Business switcher list:** the dropdown would not open under automation. Its options were not read.
- **Expense "Add Expense Item" line entry:** the control was inert in automation. Not inspected.
- **Saving behavior, validation messages after Save, and Enter-key behavior:** not exercised, because the review was on a live business. A test business is needed to check these.
- **My Account, Inventory/Invoice-print settings, Import Data and Business Tools:** skipped as out of scope.
- **Karobar native Android/iOS apps:** not reviewed (web only).
- **Role defaults:** read from the check/cross styling of each item. Entry Person was confirmed against screenshots; treat the other roles as close but not exact.
