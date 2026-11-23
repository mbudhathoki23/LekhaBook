# Parties

- **Path:** Sidebar → Parties (`/parties`). A party ledger opens at `/parties/{id}`.
- **Purpose:** keep customers and suppliers, see what each one owes or is owed, and record their transactions.
- **Layout:** on desktop, a **master–detail** view: the party list on the left and the selected party's ledger on the right. On a 390 px phone the same layout overflows sideways and the detail pane is off-screen.

## Party list

- Header: **Parties (N)**, an **Add Party** button with a ▾ menu (→ **Import Parties**), search, and a filter/sort icon.
- Each row shows an avatar (photo or initials), the name, the phone (or `---`), the amount, and **To Receive** or **To Give**.
- **Search:** "Search parties…" (by name and phone).
- **Type filter:** All Party / Customer / Supplier.
- **Balance filter:** All Payment / To Receive / To Give / Settled.
- **Sort:** Latest, Amount High→Low, Amount Low→High, Name A→Z, Name Z→A.
- Empty detail state: "Party Not Selected. Click any party to view their transactions."
- Dashboard tiles deep-link here: `/parties?party-payment-type=receivable` and `payable`.

## Add New Party (dialog)

| Label | Type | Required | Default | Notes |
|---|---|---|---|---|
| Upload Photo | image file | no | — | Can be turned on or off in Settings → Parties |
| Full Name | text | **yes** (`*`) | — | Placeholder "Enter the name of party" |
| Phone Number | text | no | — | |
| Party Category | searchable select | no | — | Categories managed in Settings → Parties |
| Party Type | radio: **Customer / Supplier** | yes | Customer | |
| *Tab: Credit Info* | | | | |
| Opening Balance | amount (Rs.) | no | 0 | Placeholder "eg. 0" |
| Payment Type | radio: **To Receive / To Give** | — | To Receive | The side of the opening balance |
| As of Date | BS date | — | today | |
| *Tab: Additional Info* | | | | |
| Address | text | no | — | |
| Email | text | no | — | |
| PAN Number | number | no | — | |

Buttons: **Save Party**, **Save & New**, **Close**.

## Party ledger (detail pane)

- Header: avatar, name, phone, and the balance labelled **Receivable** or **Payable** in large text.
- **Manage Party** menu: **Edit Party**, **Delete Party**. There is also an icon button (statement/print), which links to `/reports/party-statement?partyId=…`.
- **Send Reminder** button (WhatsApp/SMS). Not used during the review.
- **Transactions (N)** table: **Type** (for example "Payment In #615", "Sales Invoice #932", with an attachment thumbnail under the type), **Date** (BS), **Total**, **Status** (UNPAID / -- ), **Balance** (running), **Remarks**.
- Toolbar: search, **Sort**, and **+ Add Transaction** with Sales Invoice, Purchase, Payment In, Payment Out, Quotation, Sales Return, Purchase Return and **Adjust Balance**.

## Adjust Balance (party)

| Label | Type | Notes |
|---|---|---|
| Adjustment Type | radio: **Add Balance / Reduce Balance** | |
| Adjustment Amount | amount (Rs.) | |
| As of Date | BS date | today |
| Remarks | text | |
| *Preview* | Current Balance → **Adjusted Balance** | Updates live as you type |

Buttons: **Cancel**, **Confirm Adjustment**.
