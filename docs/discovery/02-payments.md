# Payment In / Payment Out

- **Opened from:** party ledger → Add Transaction → Payment In or Payment Out. Shortcuts: **Alt+I** (Payment In) and **Alt+O** (Payment Out).
- **Purpose:** record money received from a party (Payment In) or paid to a party (Payment Out). This reduces the party's receivable or payable.
- **Form:** a centered dialog with a two-column layout on desktop.

## Fields (Payment In; Payment Out is identical except the labels)

| Label | Type | Required | Default | Notes |
|---|---|---|---|---|
| Receipt Number | text | yes | next auto number (for example `1061`) | A **Manual ↔ Auto-Generate** toggle link sits next to the label |
| Date | BS date picker | yes | today | Picker shows BS months (Baisakh…Chaitra) and years 2000–2090 |
| Party Name | searchable select | yes | the current party when opened from a ledger | The party's current balance shows on the right of the label (for example `Rs. 68,260`) |
| Received Amount (In) / Paid Amount (Out) | amount, `Rs.` prefix | yes | empty, focused first | |
| Payment Method | select | yes | Cash | Options are the business's **money accounts**: Cash, Bank Account, Cheque, Online Wallet. No extra fields appear for any method |
| Remarks | textarea | no | — | "Enter remarks here…" |
| Attachment | image tile with camera icon | no | — | `accept="png, jpg, jpeg"`; no PDF |

Buttons: **Save & New**, **Save Payment In** / **Save Payment Out**, **Close** (×).

## Observations

- Focus lands on the **amount** when the party is preselected, which suits fast entry.
- On save, the entry shows in the ledger as "Payment In #<no>" with an attachment thumbnail. The running balance updates in the selected account and the party ledger.
- Validation messages were not triggered (read-only review).
