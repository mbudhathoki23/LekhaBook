# Decisions

Final choices for the open items in [AGENT_PLAN.md § 10](AGENT_PLAN.md#10-decisions-recommended-defaults). Until an item appears here, use the default listed in the plan.

| # | Decision | Choice | Decided by | Date |
|---|---|---|---|---|
| D1 | Browser offline | **The browser is online-only in v1.** `LekhaBook.Web` calls `LekhaBook.Api` directly. Offline use is through the MAUI app (Android, iOS, Windows). A browser PWA with local storage and the same sync engine can come in a later version. Keep `LekhaBook.UI` behind interfaces so that stays possible. | User | 2026-11-23 |
