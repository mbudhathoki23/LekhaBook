# Manage Accounts (Cash, Bank, Cheque, Wallet)

- **Path:** Sidebar → Manage Accounts (`/manage-account`). One account's detail opens at `/manage-account/{id}`.
- **Purpose:** cash-in-hand, bank and wallet balances, and moving money between them. **Petty cash** fits here as a Cash-type account.

## Account list

- Header: **Manage Accounts (N)**, **Add Account**, and **Total Balance** (the sum of all accounts).
- Accounts seen: **Cash**, **Bank Account**, **Cheque** and **Online Wallet**, each with its balance. More bank or wallet accounts can be added.
- Empty detail state: "Account Not Selected".

## Account detail

- Header: account name, **Current Balance**, **Adjust Balance ▾** (→ Add Money / Reduce Money / Transfer Money), and **View Report** (→ cash-in-hand or bank statement).
- **Transactions Activity (N)** table: **Type** (with party name), **Date**, **Rec/Paid Amount**, **Balance** (running), **Remarks**.
- **Type filter:** All Transactions, Sales, Purchase, Income, Payment In, Payment Out, Sales Return, Purchase Return, Expense, Add Money, Reduce Money, Transfer Balance.
- **Date filter:** All Date, Today, Yesterday, This Week, This Month, Last Month, This Fiscal Year, This Year, plus a custom BS range picker (Cancel/Apply).
- **Sort:** Latest, Oldest, Amount Low→High, Amount High→Low.

## Add New Account (dialog)

| Label | Type | Notes |
|---|---|---|
| Account Type | select: **Bank Account / Wallet** | Default Bank Account |
| Bank Name | text | Label changes for Wallet (not checked) |
| Account Holder Name | text | |
| Account Number | text | |
| Current Account Balance | amount (Rs.) | The opening balance |

Buttons: **Cancel**, **Add Account**.

## Adjust Balance → Add Money / Reduce Money (dialog title "Adjust Balance")

| Label | Type | Notes |
|---|---|---|
| Select Type | radio: **Add Money / Reduce Money** | |
| Account | select (Cash / Bank Account / Cheque / Online Wallet) | Defaults to the current account |
| Total Amount | amount (Rs.) | |
| Date | BS date | today |
| Remarks | text | |
| Attach Images | image file (png/jpg/jpeg) | |

## Transfer Money (dialog title "Transfer Balance")

| Label | Type | Notes |
|---|---|---|
| Transfer From | select (all accounts) | Defaults to the current account |
| Transfer To | select | Excludes the From account; placeholder "Select Account" |
| Total Amount | amount (Rs.) | |
| Date | BS date | |
| Remarks | text | |
| Attach Images | image file | |

Buttons: **Cancel**, **Transfer Money**.
