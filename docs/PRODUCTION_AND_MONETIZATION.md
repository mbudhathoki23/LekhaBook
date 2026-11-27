# Lekha Books — Production Deployment & Monetization Playbook

Lekha Books is architected to maximize **user acquisition at zero server cost** (Free Offline Local Mode on SQLite) while converting growing businesses into **high-margin recurring revenue** (Online Cloud Mode with real-time sync, web access, staff permissions, and live WhatsApp/Email statement links).

---

## 1. Monetization Model (Built Into the Product Funnel)

### Tier Structure (Nepal / South Asia & Global)

| Plan | Price (Nepal) | Price (Global) | What Users Get | Your Server Cost |
|---|---|---|---|---|
| **Free (Offline Local Mode)** | **Rs. 0 forever** | **\$0 forever** | Full Party Ledger, Cash/Bank/Wallet, Expense/Income, Receipts, BS/AD Calendar, Reports, 6 Languages, Manual JSON Backup — **100% offline on SQLite** | **\$0 / user** |
| **Pro Cloud** | **Rs. 299 / mo**<br>*(or Rs. 2,499 / yr)* | **\$4.99 / mo**<br>*(or \$39 / yr)* | **1-Tap Upgrade from Offline → Cloud**, automatic offline+online sync, real-time cloud backup, Android + Windows + Web Browser access, WhatsApp & Email live statement links, Excel/CSV/PDF import, up to 3 businesses | ~\$0.01 / user / mo |
| **Business / Team** | **Rs. 699 / mo**<br>*(or Rs. 5,999 / yr)* | **\$12 / mo**<br>*(or \$99 / yr)* | Everything in Pro + **up to 10 Staff members** (Partner, Manager, Accountant, Entry Person with per-module permissions & Lock Transaction Date), unlimited businesses, priority WhatsApp support | ~\$0.03 / user / mo |
| **CA / Accounting Firm Partner** | **Rs. 15,000 / yr**<br>*(10 client licenses)* | **\$199 / yr** | Bulk reseller bundle for Chartered Accountants & Tax Consultants who onboard their SMB clients onto Lekha Books and access their ledgers remotely via the Web Portal | ~\$0.20 / firm / mo |

### Why This Funnel Converts So Well
1. **Zero Friction Install**: Shopkeepers download Lekha Books and tap **"Continue Offline (Free Local Mode)"** on `/signin`. No OTP wait, no credit card, works even with weak mobile data.
2. **Natural Upgrade Triggers (Already Wired in UI)**:
   - Persistent local-mode banner + clickable **`Local only · Upgrade`** chip → `/upgrade`
   - Tapping **Staff** in Offline Mode → shows why Online Cloud Mode unlocks Accountant/Entry Person roles with a direct button to `/upgrade`
   - Fear of losing a phone (damaged/stolen phone = lost khata) → **1-Tap Upgrade** migrates every local business, party, bank account, transaction, logo, and receipt atomically via `POST /api/companies/migrate` with zero data loss!
3. **High Retention**: Once a shop puts 30+ days of party balances and staff entries into Cloud Sync, churn is extremely low (<3%/month).

---

## 2. Unit Economics & Profit Projections

### Monthly Fixed Infrastructure Cost
- **VPS (Hetzner CPX21 or DigitalOcean 2 vCPU / 4 GB RAM / 80 GB NVMe)**: **\$7 – \$12 / month** (handles 500–2,000 active small businesses easily on .NET 10 + PostgreSQL 17)
- **Domain (`lekhabooks.com`) + Cloudflare DNS**: ~\$1 / month
- **Off-site automated DB backup (Backblaze B2 / Cloudflare R2)**: <\$1 / month
- **Total Fixed Cost**: **~\$10 – \$15 / month (~Rs. 1,500 – 2,000 / month)**

### Revenue Milestones
- **Break-even**: Just **7 monthly Pro users** (or **10 annual Pro users**) covers the entire year's server bill.
- **100 Annual Pro Users**: **Rs. 2,49,900 / year** (Gross margin **>90%**)
- **500 Paying Businesses (mix of Pro + Business)**: **Rs. 16,00,000+ / year** (~Rs. 1.35 lakh/month)
- **2,000 Paying Businesses**: **Rs. 65,00,000+ / year** (~Rs. 5.4 lakh/month)

---

## 3. Payment Gateway Integration Plan (Next Step Before Public Paid Launch)

1. **Nepal Domestic Payments** (Highest conversion in Nepal):
   - **eSewa ePay v2** & **Khalti KPG-2** & **Fonepay Dynamic QR**: Add `POST /api/billing/checkout` + webhook verification endpoint on `LekhaBook.Api`.
   - Store a `Subscription` table (`UserId`, `Plan`, `Status`, `CurrentPeriodEndUtc`, `Provider`, `ProviderReference`) in `PostgresDbContext`.
   - Start every newly upgraded Online Cloud user on a **14-day or 30-day Free Cloud Trial** so they experience sync + WhatsApp statements before paying!
2. **International / Play Store Compliance**:
   - Use **Google Play Billing** for in-app subscriptions inside the Android Play Store build (required by Google Play policy for digital subscriptions sold inside the app), and **Stripe** on the Web portal (`app.lekhabooks.com`).

---

## 4. Production VPS Deployment (5-Minute Setup)

All production files are ready in [`deploy/`](../deploy/):
- [`deploy/Dockerfile.api`](../deploy/Dockerfile.api) — .NET 10 API with durable `/data/attachments` & `/data/keys`
- [`deploy/Dockerfile.web`](../deploy/Dockerfile.web) — .NET 10 Blazor Web App with persistent Data Protection keys
- [`deploy/docker-compose.prod.yml`](../deploy/docker-compose.prod.yml) — PostgreSQL 17 + API + Web + Caddy
- [`deploy/Caddyfile`](../deploy/Caddyfile) — Automatic Let's Encrypt HTTPS TLS + security headers + gzip/zstd
- [`deploy/.env.example`](../deploy/.env.example)

### Step-by-Step Server Launch

1. **Provision an Ubuntu 24.04 VPS** (2 vCPU, 4 GB RAM) and install Docker + Docker Compose.
2. **Point DNS A records** to your VPS IP:
   - `api.yourdomain.com` → `<VPS_IP>`
   - `app.yourdomain.com` → `<VPS_IP>`
3. **Clone the repo and configure secrets**:
   ```bash
   cp deploy/.env.example deploy/.env
   nano deploy/.env
   ```
   Set a strong 32+ character `POSTGRES_PASSWORD`, set `API_DOMAIN=api.yourdomain.com`, `WEB_DOMAIN=app.yourdomain.com`, and for the very first boot set `MIGRATE_ON_STARTUP=true`.
4. **Launch the stack**:
   ```bash
   docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env up -d --build
   ```
5. **Verify health**:
   ```bash
   curl https://api.yourdomain.com/health
   curl https://api.yourdomain.com/health/ready
   ```
   After the first boot finishes migrating, change `MIGRATE_ON_STARTUP=false` in `deploy/.env` and restart the `api` container.
6. **Set up automated daily PostgreSQL backups** (cron at 2:00 AM daily):
   ```bash
   0 2 * * * docker exec $(docker compose -f /opt/lekhabook/deploy/docker-compose.prod.yml ps -q postgres) pg_dump -U lekhabook lekhabook | gzip > /backups/lekhabook-$(date +\%F).sql.gz
   ```
   Sync `/backups` nightly to Cloudflare R2 or Backblaze B2 using `rclone`.

---

## 5. Building Production Apps (Android Play Store & Windows)

Use [`scripts/publish-release.ps1`](../scripts/publish-release.ps1) to compile Release binaries pointing to your live API domain without editing source code:

```powershell
./scripts/publish-release.ps1 -ApiBaseUrl "https://api.yourdomain.com/" -Version "1.0.0" -BuildNumber 1
```

### Android Play Store Signing Checklist
1. Generate an upload keystore **once** (store it offline + encrypted backup; never commit it to Git):
   ```bash
   keytool -genkeypair -v -keystore lekhabooks-release.keystore -alias lekhabooks -keyalg RSA -keysize 2048 -validity 10000
   ```
2. Build a signed `.aab` for Google Play Console:
   ```powershell
   dotnet publish src/LekhaBook.App/LekhaBook.App.csproj -f net10.0-android -c Release `
     -p:ApiBaseUrl="https://api.yourdomain.com/" `
     -p:ApplicationDisplayVersion="1.0.0" -p:ApplicationVersion=1 `
     -p:AndroidKeyStore=true `
     -p:AndroidSigningKeyStore="C:\secrets\lekhabooks-release.keystore" `
     -p:AndroidSigningKeyAlias="lekhabooks" `
     -p:AndroidSigningKeyPass="$env:LB_KEY_PASS" `
     -p:AndroidSigningStorePass="$env:LB_STORE_PASS"
   ```
3. **Play Store Listing & ASO Tips**:
   - Title: `Lekha Books: Offline Khata, Party Ledger & Cashbook`
   - Keywords: Bikram Sambat (BS) ledger, offline accounting, party khata, cash book, bank book, Nepal business bookkeeping, multi-user staff ledger
   - Highlight: **Works 100% Offline**, **Bikram Sambat (BS) + AD Dates**, **6 Languages (English, Nepali, Bengali, Arabic, Chinese, Japanese)**, **Photo & Document Scan Proof**, **WhatsApp Statement Links**.

---

## 6. Go-To-Market: Getting Your First 100 Paying Customers

1. **Direct Local Onboarding (Fastest First 25 Customers)**:
   - Visit 50 nearby wholesalers, hardware shops, pharmacies, and distributors.
   - Install Lekha Books in **Offline Local Mode** on their phone in 30 seconds, import their existing Excel/CSV party list, and show them a live **WhatsApp Party Statement** with their shop logo.
   - Offer **Founder's Annual Deal: Rs. 1,499/year** for the first 50 shops!
2. **Short-Form Video Demos (TikTok / Facebook Reels / YouTube Shorts)**:
   - 30-second videos in Nepali & English:
     - *"No internet in your shop? Still record every Udharo & Cash entry — auto-syncs when Wi-Fi returns!"*
     - *"Stop staff from back-dating cash entries with Lock Transaction Date!"*
     - *"Send professional Bikram Sambat party statements on WhatsApp in 2 taps!"*
3. **CA / Auditor Partnerships (Highest Leverage)**:
   - Give local accounting/audit firms free Business accounts for their own office + 30% recurring affiliate commission for every client shop they onboard.
4. **In-App Referral Loop**:
   - "Share Lekha Books with another business → Both get 1 month of Online Cloud Sync free!"
