# End-to-end workflows to reproduce in LekhaBook

Steps follow what Karobar shows. "→" means a click, tap or shortcut.

## W1. Add a customer with an opening balance
1. Parties → **Add Party** (Alt+N).
2. Full Name, Phone, (Category), Party Type = Customer.
3. Credit Info: Opening Balance, **To Receive**, As of Date (BS, today).
4. (Additional Info: Address, Email, PAN.) → **Save Party**, or **Save & New** for the next party.
5. The party appears at the top of the list with "Rs. X To Receive".

## W2. Receive money from a party, with a photo of the receipt
1. Parties → select party → **Add Transaction → Payment In** (or Alt+I from anywhere, then pick the party).
2. Receipt No. auto-fills; Date = today; the party's balance shows beside the party name.
3. Enter **Received Amount**, choose **Payment Method** (Cash / Bank / Cheque / Wallet), add Remarks.
4. Tap the camera tile → take a photo or pick an image.
5. **Save Payment In**. The ledger row "Payment In #N" shows a thumbnail. The party balance and the chosen account balance both update.

## W3. Pay a supplier
Same as W2 using **Payment Out** (Alt+O). The party's payable goes down and the money account goes down.

## W4. Correct a party balance without a payment
Party → Add Transaction → **Adjust Balance** → Add or Reduce, amount, date, remarks → the preview shows the adjusted balance → **Confirm Adjustment**.

## W5. Petty cash expense with a bill photo
Expense → **Add New Expense** (Alt+E) → Category (for example Fuel), Total Amount, Payment Method = Cash (petty cash account), Remarks, photo → **Save Expense**. The cash balance goes down. It appears in the Cash In Hand Statement and the Expense Category report.

## W6. Deposit cash into the bank
Manage Accounts → Cash → **Adjust Balance → Transfer Money** → From Cash, To Bank Account, amount, date, remarks, optional slip photo → **Transfer Money**. Both account activity lists show the transfer.

## W7. Top up or reduce an account
Manage Accounts → account → Adjust Balance → **Add Money / Reduce Money** → amount, date, remarks, image.

## W8. Statement for a party, shared as PDF
Party → statement icon (or Reports → Party Statement) → pick a date range → Normal or Accounting view → **Print PDF** or **Download Excel** → share.

## W9. Daily review
Reports → **Day Book** (Today) and **Cash In Hand Statement** (Date, Particular, Remarks, Money In, Money Out, running Balance).

## W10. Add a staff member to the business
Manage Staffs → **Add New Staff** → name, role (for example Entry Person) → optionally Manage Permissions → Edit Permission → **Add Staff** → the staff member accepts → Status becomes Accepted.

## W11. Switch business
Sidebar business switcher → choose another business. All screens re-scope to that business.
