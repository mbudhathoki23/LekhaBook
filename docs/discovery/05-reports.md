# Reports

- **Path:** Sidebar → Reports (`/reports`). Shortcut **Shift+R**.
- **Groups (filter chips):** All Reports, Transactions, Parties, Inventory, Income Expense, Business Status.
- **Every report shares a toolbar:** **Go Back**, **Print PDF**, **Download Excel**, **View PDF Options**, a date-range chip (This Month / Today …) and search.

| Group | Report | Route | v1? |
|---|---|---|---|
| Transactions | Sales / Purchase / Sales Return / Purchase Return | `/reports/sales` … | No |
| Transactions | **Day Book**: all daily transactions | `/reports/day-book` | **Yes** |
| Transactions | **All Transactions** | `/reports/all-transactions` | **Yes** |
| Transactions | Profit and Loss | `/reports/profit-and-loss` | No |
| Parties | **Party Statement**: one party's transactions | `/reports/party-statement?partyId=` | **Yes** |
| Parties | **All Party Report**: receivable/payable of every party | `/reports/all-party` | **Yes** |
| Inventory | Item details, item list, low stock, stock quantity | `/reports/item-*` | No |
| Income Expense | **Income Expense Report**; **Expense Category**; **Income Category** | `/reports/income-expense-transaction` … | **Yes** |
| Business Status | **Cash In Hand Statement** | `/reports/cash-in-hand-statement` | **Yes** |
| Business Status | **Bank Statement** | `/reports/bank-statement` | **Yes** |
| Business Status | Discount, Tax Sales, Tax Purchase | | No |

## Layouts seen

- **Party Statement:**
  - Party picker ("Search for party").
  - Date range.
  - A **Normal View / Accounting View** toggle (the accounting view is likely debit/credit columns).
  - A "PDF With Item" option.
- **Cash In Hand Statement:** columns **Date, Particular, Notes/Remarks, Money In, Money Out, Balance**.
- **All Party Report:**
  - Columns **Party Name, Contact, Receivable, Payable**.
  - Filters: All Party / All Due Types / All Categories.
- **Day Book:** defaults to **Today**; "No Transactions Found" empty state.
