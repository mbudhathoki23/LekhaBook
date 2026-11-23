# Gaps in the reference app that LekhaBook should improve

| # | Observation in Karobar web | LekhaBook approach |
|---|---|---|
| G1 | Phone-width browser layout overflows sideways, and the party ledger is unreachable | Mobile-first layouts: list → full-screen detail on phones, master–detail only at ≥ 960 px; bottom nav and a "+" button |
| G2 | Attachments accept png/jpg/jpeg only | Also accept PDF; several attachments per entry; compress on device |
| G3 | "Payment Method" shows only the account type names. With several banks or wallets, the choice can be unclear | Show each account by name (for example "NIC Asia ••1234") with its balance in the picker |
| G4 | Cheque is just an account; no cheque no. or cheque date | Optional Cheque No. and Cheque Date fields when the account type is Cheque; a "Cheque clearing" transfer to bank |
| G5 | No visible offline state on web | A status chip (Online · Synced / Offline · N pending) and an offline-capable app |
| G6 | Enter-key flow unverified; Alt shortcuts only open forms | Full Enter → next field flow, Ctrl+S to save, and the same Alt/Shift shortcuts for familiarity |
| G7 | Expense/Income forms have a single total, with item lines hidden behind a link | Keep the single total for speed; item lines optional (later) |
| G8 | Validation and edit history not visible | An audit trail on every edit, delete or adjustment; a "Lock Transaction Date" permission enforced on the server |
| G9 | Party names mix English and Nepali in one field | Search that matches either script; optionally a separate "Name (Nepali)" field |
| G10 | Header crowded on phones | Move language, shortcuts and theme into a profile/settings menu on small screens |
