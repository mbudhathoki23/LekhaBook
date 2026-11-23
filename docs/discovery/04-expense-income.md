# Expense and Other Income

- **Paths:** Sidebar → Expense (`/expense`) and Sidebar → Other Income (`/income`). Shortcut **Alt+E** opens Add Expense.
- **Empty state:** "Create Your First Expense" / "Create Your First Income", with **Add New Expense** / **Add New Income**.
- The Other Income transaction type can be switched on or off in Settings → Transactions.

## Add Expense (dialog)

| Label | Type | Required | Default | Notes |
|---|---|---|---|---|
| Expense No. | text | yes | auto number | Manual/Auto toggle |
| Date | BS date | yes | today | |
| Expense Category | searchable select | yes | — | Seeded: General, Health, Recharge, Food, Entertainment, Fuel, Shopping, Travel. Managed in Settings → Transactions → Manage Expense Categories |
| Add Expense Item | link → item lines | no | — | Optional item breakdown (not inspected) |
| Total Amount | amount (Rs.) | yes | — | |
| Payment Method | select (money accounts) | yes | Cash | |
| Remarks | textarea | no | — | |
| Attachment | image tile | no | — | png/jpg/jpeg |

Buttons: **Save Expense**, **Close**.

## Add Income (dialog)

Same layout as Add Expense, with these differences:
- **Income No.** replaces Expense No.
- **Income Category** offers the seeded categories Salary, Investment and Commission.
- **Add Income Item** replaces Add Expense Item.
- The save button is **Save Income**.
