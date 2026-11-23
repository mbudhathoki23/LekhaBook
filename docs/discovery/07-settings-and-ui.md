# Settings, localization and UI behavior

## Settings (`/settings/...`)

Sections: **General**, **My Account**, **Business Profile**, and **Feature Settings** (Parties, Inventory, Transactions, Invoice Print).

### General settings

| Setting | Options seen |
|---|---|
| Appearance | Light, Classic, Dark theme |
| Language | English (Nepali available from the header switch: English / नेपाली) |
| Currency | `Rs.`; Currency Position: Start |
| Calendar | **AD / BS** |
| Date Format | 4 presets (for example `2083 Man 07`) |
| Time Format | for example `10:39 PM` |
| Number Format | 2 presets (for example `10,00,000` lakh grouping) |
| Privacy Mode | Hides business stats on the home page and item purchase prices |
| App Lock | Lock screen for business access |

### Business Profile

- **Basic:** Photo/logo, Business Name, Contact Number, Email, Business Category (for example General Stores (Kirana)), Business Type (Retailer / Wholesaler / Distributor / Manufacturer / Service).
- **Address:** Province → District → Municipality (cascading Nepal lists), and Street Address.
- **Financial:** Registration Number, Bank Accounts (links to Manage Accounts).
- **Save Details**, and a **Danger Area** (delete business).

### Feature settings

- **Parties:** Enable Party Category (Manage Party Categories); Enable Upload Party Image.
- **Transactions:**
  - Set Cash Sale by Default
  - Enable Due Date Reminder
  - Enable Other Income Transaction
  - Manage Income Categories and Expense Categories
  - **Enable Transaction Prefixes** (Sales, Sales Return, Payment In, Quotation prefixes)
  - Additional Charges and Round Off
  - **Reset Transaction Serial Number**

## Keyboard shortcuts (header keyboard icon, or Shift+K)

| Adding data | | Going to pages | |
|---|---|---|---|
| Sales Invoice | Alt+S | Dashboard | Shift+D |
| Purchase Invoice | Alt+P | Parties | Shift+P |
| **Payment In** | **Alt+I** | Inventory | Shift+I |
| **Payment Out** | **Alt+O** | Expense | Shift+E |
| Sales Return | Alt+C | Quick POS | Shift+Q |
| Purchase Return | Alt+D | Settings | Shift+S |
| Quotation | Alt+Q | Reports | Shift+R |
| **Expense** | **Alt+E** | Help & Support | Shift+H |
| Add Item | Alt+M | Keyboard Shortcuts | Shift+K |
| **Party** | **Alt+N** | Toggle Sidebar | Shift+M |
| | | Close Dialog | Esc |

Enter-to-next-field was **not tested** (it could save a real entry).

## Mobile web (390×844)

- The sidebar collapses into a hamburger drawer. The header keeps the flag, keyboard, bell and theme icons and the user name, which crowds the bar.
- The **Parties master–detail layout doesn't adapt:** the list fills the width, the page scrolls sideways, and the ledger pane is off-screen.
- There is no bottom navigation and no floating "+" button. On the dashboard, "New / Add Sales / Add Purchase / Add More" act as quick actions.
- Conclusion: Karobar treats the browser as a **desktop** tool, and phones use its native app. LekhaBook must make the phone layout work well in both the browser and the app.

## Notifications

There is a bell panel with a filter. It showed only stock-out alerts, which are out of scope.
