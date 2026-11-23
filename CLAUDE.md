# LekhaBook

Mobile-first bookkeeping app (Party ledger, Bank, Petty Cash, with photo attachments). It is built with Blazor: a .NET MAUI Hybrid app for Android, iOS and Windows, plus a Blazor Web app for browsers. It works offline on SQLite and syncs to PostgreSQL through an ASP.NET Core API.

**Follow [docs/AGENT_PLAN.md](docs/AGENT_PLAN.md).** Work its phases in order and meet each phase's exit criteria before moving on.

Key rules (details in the plan):
- Phase 1 (reviewing https://my.karobarapp.com/ in Edge) is **read-only**. The user logs in manually (phone OTP / Google); never type credentials or OTPs; never save, delete or send reminders in a real business.
- Copy workflows only, never Karobar branding, text or assets.
- Shared UI lives in `LekhaBook.UI` and depends only on interfaces (`IDataService`, `IAttachmentService`, …).
- Money is `decimal`; balances are always computed from transactions, never stored or synced.
- Every tenant table has `CompanyId`; the server enforces permissions.
- Verify library versions and APIs with Context7 before pinning them.
- Record decisions in `docs/DECISIONS.md`.
