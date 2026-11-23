# Manage Staffs: multi-user, roles and permissions

- **Path:** Sidebar → Manage Staffs (`/manage-staffs`).
- **Intro banner (paraphrased):** add your staff to manage the business together, and customize their roles and permission access as needed.
- **Staff table:** **Staff Name, Role, Status** (for example Accepted), **Joined Date** (BS), and **Action** (edit ✎, delete 🗑, or 🔒 "No Permission" when you can't manage that staff member).

## Add New Staff (dialog)

| Label | Type | Notes |
|---|---|---|
| Staff Name | text | |
| Select Role | radio: **Partner, Manager, Accountant, Sales Person, Stock Manager, Entry Person** | |
| Manage Permissions | link | Opens the permission matrix for the chosen role, with **Edit Permission** to customize it, and **Done** |

Buttons: **Cancel**, **Add Staff**.

> **How the invite reaches the staff member** (phone, link or QR) was not visible on the web form; the mobile app probably handles that step. **Status: Accepted** suggests the staff member has to accept the invite.

## Permission matrix (module → actions)

| Module | Actions |
|---|---|
| Sales Vouchers (Sales, Quotation, **Payment In**, Sales Return, **Other Income**) | Create, Delete, Edit, View |
| Purchase Vouchers (Purchase, **Payment Out**, Purchase Return) | Create, Delete, Edit, View |
| Income & Expenses | Create, Delete, Edit, View |
| Inventory (Item, Category) | Create, Delete, Edit, View; Edit/Delete Item Category; View Item Category |
| Stock Adjustments | Create, Delete, Edit |
| Customers | Create, Delete, Edit, View |
| Suppliers | Create, Delete, Edit, View |
| App Features | Import Bulk, Use Business Cards, Use Greeting Cards |
| Manage Staffs | Create, Delete, Edit, View |
| Business Settings | Edit Business, Edit Invoice / Item / Party / Transaction Settings, Delete Business |
| Manage Accounts (Bank, Wallet) | Add, Edit, Delete, View Account; Create Transaction Adjustment; Edit/Delete Transaction Adjustment |
| Reports | View Business Status, Homepage Stats, Income Expense, Item, Party, Transaction reports |
| Notebook | Create, View, Edit, Delete |
| Other | **Manage Party Adjustments**, **Hide Purchase Price**, **Lock Transaction Date** |

## Role defaults

Read from the ✓/✗ styling of each item. Entry Person was also checked against a screenshot. The other roles are close but not exact.

> "Hide Purchase Price" and "Lock Transaction Date" are **restrictions**: ✓ applies the restriction. So ✗ on Partner, Manager and Accountant means those roles are **not** restricted.

- **Partner:** everything except Delete Business and Notebook. Unrestricted by Hide Purchase Price and Lock Transaction Date.
- **Manager:** create, view and edit everywhere, but **no deletes**, no staff management, no business or settings edits, and no add/edit/delete of accounts.
- **Accountant:** everything except staff management, Edit/Delete Business, Delete Account and Notebook.
- **Entry Person:**
  - Can **create and view only** sales/Payment In, purchase/Payment Out, income/expense, customers, suppliers, items and stock adjustments.
  - Can't edit or delete them.
  - Also has Import Bulk and **Edit Invoice / Item / Party / Transaction Settings**. Confirmed by screenshot; surprising, because Transaction Settings include prefixes and "Reset Transaction Serial Number".
  - Has **no** access to Manage Accounts (not even View Account), **no reports at all** (including homepage stats), and no staff management.
- **Sales Person / Stock Manager:** create and view in their own area only (sales and customers; purchase, items and suppliers).

## What LekhaBook should take from this

- Use a **per-module CRUD permission set** with role templates that can be customized per user. That is more flexible than the fixed 5-role table in AGENT_PLAN § 4.2.
- "**Lock Transaction Date**" (stop back-dating) and "**Manage Party Adjustments**" are useful controls for a data-entry workforce. Add them.
- Don't copy the Entry Person default for settings: in LekhaBook, numbering and settings edits should belong to Accountant and above.
- Entry Person sees no balances or reports in Karobar. AGENT_PLAN § 4.2 currently gives Data Entry "balances + own entries", so decide which behavior we want. A data-entry user on a phone usually needs to see a party's balance while taking a payment.
