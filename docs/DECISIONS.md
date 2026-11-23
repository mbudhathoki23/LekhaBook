# Decisions

Final choices for the open items in [AGENT_PLAN.md § 10](AGENT_PLAN.md#10-decisions-recommended-defaults). Until an item appears here, use the default listed in the plan.

| # | Decision | Choice | Decided by | Date |
|---|---|---|---|---|
| D1 | Browser offline | **The browser is online-only in v1.** `LekhaBook.Web` calls `LekhaBook.Api` directly. Offline use is through the MAUI app (Android, iOS, Windows). A browser PWA with local storage and the same sync engine can come in a later version. Keep `LekhaBook.UI` behind interfaces so that stays possible. | User | 2026-11-23 |
| D2 | Date system | **Store AD; display BS by default, AD selectable per company.** BS date picker; lakh number grouping (`10,00,000`); currency `Rs.` Confirmed by discovery: Karobar users work in BS. | User (scope approval) | 2026-11-23 |
| — | v1 scope | **Approved** as in `discovery/00-summary.md` § 4. AGENT_PLAN §§ 4.2, 5.1, 5.2, 5.4, 8.3 and 8.4 updated to match: Cheque/Wallet accounts, Karobar-style transaction names, per-module permission matrix with role templates (Owner, Partner, Manager, Accountant, Entry Person), Manual/Auto numbering with prefixes, and Karobar-compatible shortcuts. Entry Person sees balances but can't change settings (unlike Karobar). | User | 2026-11-23 |
