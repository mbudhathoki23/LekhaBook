using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using LekhaBook.Api.Security;
using LekhaBook.Contracts;
using LekhaBook.Data.Postgres;
using LekhaBook.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace LekhaBook.Api.Endpoints;

/// <summary>
/// Server-side subscription billing for Online Cloud Mode (eSewa ePay v2, Khalti KPG-2, Stripe Checkout + Webhooks,
/// and Google Play Billing). Every online user automatically receives a 14-day Free Cloud Trial.
/// Payment activation is idempotent by TransactionRef so retries/webhooks never double-extend subscriptions.
/// </summary>
public static class BillingEndpoints
{
    public const string GatewayHttpClient = "billing-gateways";

    public static void MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/billing").RequireAuthorization();
        api.MapGet("/status", GetStatus);
        api.MapPost("/checkout", StartCheckout);
        api.MapPost("/verify", VerifyPayment);

        // Unauthenticated webhook verified strictly by Stripe's HMAC-SHA256 Stripe-Signature header.
        app.MapPost("/api/billing/webhooks/stripe", HandleStripeWebhook).AllowAnonymous();
    }

    // ---------------- Helpers used across CompanyEndpoints for limit enforcement ----------------

    public static bool IsEnforcementEnabled(IConfiguration config) =>
        config.GetValue("Billing:EnforceLimits", false);

    public static async Task<Subscription> GetOrCreateSubscriptionAsync(PostgresDbContext db, Guid userId, TimeProvider clock)
    {
        var existing = await db.Subscriptions.SingleOrDefaultAsync(s => s.UserId == userId);
        if (existing is not null)
        {
            return existing;
        }

        var trial = BillingCatalog.CreateTrial(userId, clock.GetUtcNow().UtcDateTime);
        db.Subscriptions.Add(trial);
        await db.SaveChangesAsync();
        return trial;
    }

    public static ProblemHttpResult PaymentRequired(string detail) =>
        TypedResults.Problem(
            title: "Subscription required",
            detail: detail,
            statusCode: StatusCodes.Status402PaymentRequired);

    // ---------------- GET /api/billing/status ----------------

    private static async Task<Results<Ok<BillingStatusResponse>, UnauthorizedHttpResult>> GetStatus(
        HttpContext http, PostgresDbContext db, IConfiguration config, IHostEnvironment env, TimeProvider clock)
    {
        if (CompanyAccess.UserId(http.User) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        var sub = await GetOrCreateSubscriptionAsync(db, userId, clock);
        return TypedResults.Ok(await BuildStatusAsync(db, sub, config, env, clock));
    }

    // ---------------- POST /api/billing/checkout ----------------

    private static async Task<Results<Ok<StartCheckoutResponse>, ValidationProblem, ProblemHttpResult, UnauthorizedHttpResult>> StartCheckout(
        StartCheckoutRequest request,
        HttpContext http,
        PostgresDbContext db,
        AuthDbContext auth,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        IHostEnvironment env,
        TimeProvider clock)
    {
        if (CompanyAccess.UserId(http.User) is not { } userId || await auth.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId) is not { } user)
        {
            return TypedResults.Unauthorized();
        }

        if (BillingCatalog.ParsePaidPlan(request.PlanCode) is not { } paidPlan)
        {
            return Invalid("planCode", "Choose a valid paid plan (ProMonthly, ProYearly, BusinessMonthly, BusinessYearly).");
        }

        if (BillingCatalog.ParseProvider(request.Provider) is not { } provider)
        {
            return Invalid("provider", "Choose a valid payment provider (ESewa, Khalti, Stripe, Manual).");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var spec = BillingCatalog.SpecFor(paidPlan);
        var sub = await GetOrCreateSubscriptionAsync(db, userId, clock);
        var txRef = $"LB-{now:yyyyMMdd}-{Guid.CreateVersion7():N}"[..32];
        var currency = provider == PaymentProvider.Stripe ? "USD" : "NPR";
        var amount = provider == PaymentProvider.Stripe ? spec.PriceUsd : spec.PriceNpr;

        var returnBase = ResolveReturnBaseUrl(request.ReturnBaseUrl, config);

        // Sandbox / Dev test activation (never allowed in Production unless explicitly enabled).
        if (provider == PaymentProvider.Manual)
        {
            if (!IsSandboxTestModeEnabled(config, env))
            {
                return TypedResults.Problem("Sandbox test checkout is disabled in production.", statusCode: StatusCodes.Status403Forbidden);
            }

            var payment = new PaymentTransaction
            {
                UserId = userId,
                SubscriptionId = sub.Id,
                Plan = paidPlan,
                Provider = PaymentProvider.Manual,
                Status = PaymentStatus.Completed,
                Amount = amount,
                Currency = currency,
                TransactionRef = txRef,
                ProviderRef = $"SANDBOX-{Guid.CreateVersion7():N}",
                CreatedAtUtc = now,
                CompletedAtUtc = now,
            };
            db.PaymentTransactions.Add(payment);
            BillingCatalog.ApplyPaidPlan(sub, paidPlan, PaymentProvider.Manual, now);
            await db.SaveChangesAsync();

            return TypedResults.Ok(new StartCheckoutResponse(payment.Id, payment.TransactionRef, provider.ToString(), CompletedImmediately: true));
        }

        if (provider == PaymentProvider.ESewa)
        {
            var esewa = ReadESewa(config);
            if (!esewa.Enabled)
            {
                return TypedResults.Problem("eSewa payments are not configured on this server yet.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var payment = CreatePendingPayment(userId, sub.Id, paidPlan, provider, amount, currency, txRef, now);
            db.PaymentTransactions.Add(payment);
            await db.SaveChangesAsync();

            var amtStr = PaymentSignatures.FormatESewaAmount(amount);
            var sig = PaymentSignatures.ESewaCheckoutSignature(amount, txRef, esewa.ProductCode, esewa.SecretKey);
            var fields = new Dictionary<string, string>
            {
                ["amount"] = amtStr,
                ["tax_amount"] = "0",
                ["total_amount"] = amtStr,
                ["transaction_uuid"] = txRef,
                ["product_code"] = esewa.ProductCode,
                ["product_service_charge"] = "0",
                ["product_delivery_charge"] = "0",
                ["success_url"] = $"{returnBase}/billing/callback?provider=ESewa",
                ["failure_url"] = $"{returnBase}/billing/callback?provider=ESewa&failed=1",
                ["signed_field_names"] = "total_amount,transaction_uuid,product_code",
                ["signature"] = sig,
            };

            return TypedResults.Ok(new StartCheckoutResponse(
                payment.Id, txRef, provider.ToString(),
                FormActionUrl: esewa.FormUrl,
                FormFields: fields));
        }

        if (provider == PaymentProvider.Khalti)
        {
            var khalti = ReadKhalti(config);
            if (!khalti.Enabled)
            {
                return TypedResults.Problem("Khalti payments are not configured on this server yet.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var payment = CreatePendingPayment(userId, sub.Id, paidPlan, provider, amount, currency, txRef, now);
            var client = httpFactory.CreateClient(GatewayHttpClient);
            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{khalti.BaseUrl}/epayment/initiate/");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Key", khalti.SecretKey);
            msg.Content = JsonContent.Create(new
            {
                return_url = $"{returnBase}/billing/callback?provider=Khalti&ref={Uri.EscapeDataString(txRef)}",
                website_url = returnBase,
                amount = (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero),
                purchase_order_id = txRef,
                purchase_order_name = $"Lekha Books {spec.Name} ({spec.BillingCycle})",
                customer_info = new { name = user.DisplayName ?? user.Email, email = user.Email },
            });

            using var resp = await client.SendAsync(msg);
            var raw = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                return TypedResults.Problem("Khalti could not start the payment session. Please try again.", statusCode: StatusCodes.Status502BadGateway);
            }

            using var doc = JsonDocument.Parse(raw);
            var pidx = doc.RootElement.TryGetProperty("pidx", out var pEl) ? pEl.GetString() : null;
            var paymentUrl = doc.RootElement.TryGetProperty("payment_url", out var uEl) ? uEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(pidx) || string.IsNullOrWhiteSpace(paymentUrl))
            {
                return TypedResults.Problem("Khalti returned an incomplete checkout response.", statusCode: StatusCodes.Status502BadGateway);
            }

            payment.ProviderRef = pidx;
            db.PaymentTransactions.Add(payment);
            await db.SaveChangesAsync();

            return TypedResults.Ok(new StartCheckoutResponse(payment.Id, txRef, provider.ToString(), RedirectUrl: paymentUrl));
        }

        if (provider == PaymentProvider.Stripe)
        {
            var stripe = ReadStripe(config);
            if (!stripe.Enabled)
            {
                return TypedResults.Problem("Stripe card payments are not configured on this server yet.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var payment = CreatePendingPayment(userId, sub.Id, paidPlan, provider, amount, currency, txRef, now);
            var client = httpFactory.CreateClient(GatewayHttpClient);
            using var msg = new HttpRequestMessage(HttpMethod.Post, "https://api.stripe.com/v1/checkout/sessions");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stripe.SecretKey);
            msg.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["mode"] = "payment",
                ["client_reference_id"] = txRef,
                ["customer_email"] = user.Email ?? string.Empty,
                ["success_url"] = $"{returnBase}/billing/callback?provider=Stripe&session_id={{CHECKOUT_SESSION_ID}}",
                ["cancel_url"] = $"{returnBase}/billing?cancelled=1",
                ["line_items[0][price_data][currency]"] = "usd",
                ["line_items[0][price_data][unit_amount]"] = ((long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture),
                ["line_items[0][price_data][product_data][name]"] = $"Lekha Books — {spec.Name} ({spec.BillingCycle})",
                ["line_items[0][quantity]"] = "1",
            });

            using var resp = await client.SendAsync(msg);
            var raw = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                return TypedResults.Problem("Stripe could not start the checkout session. Please try again.", statusCode: StatusCodes.Status502BadGateway);
            }

            using var doc = JsonDocument.Parse(raw);
            var sessionId = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var url = doc.RootElement.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(url))
            {
                return TypedResults.Problem("Stripe returned an incomplete checkout response.", statusCode: StatusCodes.Status502BadGateway);
            }

            payment.ProviderRef = sessionId;
            db.PaymentTransactions.Add(payment);
            await db.SaveChangesAsync();

            return TypedResults.Ok(new StartCheckoutResponse(payment.Id, txRef, provider.ToString(), RedirectUrl: url));
        }

        return Invalid("provider", "Unsupported checkout provider.");
    }

    // ---------------- POST /api/billing/verify ----------------

    private static async Task<Results<Ok<VerifyPaymentResponse>, ValidationProblem, ProblemHttpResult, UnauthorizedHttpResult>> VerifyPayment(
        VerifyPaymentRequest request,
        HttpContext http,
        PostgresDbContext db,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        IHostEnvironment env,
        TimeProvider clock)
    {
        if (CompanyAccess.UserId(http.User) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        if (BillingCatalog.ParseProvider(request.Provider) is not { } provider)
        {
            return Invalid("provider", "Unknown payment provider.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var sub = await GetOrCreateSubscriptionAsync(db, userId, clock);

        if (provider == PaymentProvider.ESewa)
        {
            var esewa = ReadESewa(config);
            if (!esewa.Enabled || string.IsNullOrWhiteSpace(request.ESewaData))
            {
                return Invalid("eSewaData", "Missing eSewa payment confirmation data.");
            }

            var decoded = PaymentSignatures.ParseAndVerifyESewaCallback(request.ESewaData, esewa.SecretKey);
            if (decoded is null || !decoded.SignatureValid)
            {
                return TypedResults.Problem("eSewa payment signature verification failed.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (!string.Equals(decoded.Status, "COMPLETE", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(decoded.ProductCode, esewa.ProductCode, StringComparison.Ordinal))
            {
                return TypedResults.Problem($"eSewa payment was not completed (status: {decoded.Status}).", statusCode: StatusCodes.Status400BadRequest);
            }

            var payment = await db.PaymentTransactions.SingleOrDefaultAsync(p => p.TransactionRef == decoded.TransactionUuid && p.UserId == userId);
            if (payment is null)
            {
                return TypedResults.Problem("Payment record not found.", statusCode: StatusCodes.Status404NotFound);
            }

            if (!decimal.TryParse(decoded.TotalAmount.Replace(",", string.Empty), NumberStyles.Number, CultureInfo.InvariantCulture, out var paidAmount)
                || paidAmount != payment.Amount)
            {
                return TypedResults.Problem("Paid amount does not match the order total.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (payment.Status == PaymentStatus.Completed)
            {
                return TypedResults.Ok(new VerifyPaymentResponse(true, "Subscription is already active.", await BuildStatusAsync(db, sub, config, env, clock)));
            }

            // Optional server-to-server status confirmation (on by default when live; can be bypassed in offline unit tests).
            if (esewa.ConfirmStatusRemote)
            {
                var client = httpFactory.CreateClient(GatewayHttpClient);
                var statusUrl = $"{esewa.StatusUrl}?product_code={Uri.EscapeDataString(esewa.ProductCode)}&total_amount={Uri.EscapeDataString(PaymentSignatures.FormatESewaAmount(payment.Amount))}&transaction_uuid={Uri.EscapeDataString(payment.TransactionRef)}";
                using var resp = await client.GetAsync(statusUrl);
                if (!resp.IsSuccessStatusCode)
                {
                    return TypedResults.Problem("Could not confirm transaction status with eSewa.", statusCode: StatusCodes.Status502BadGateway);
                }

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var remoteStatus = doc.RootElement.TryGetProperty("status", out var sEl) ? sEl.GetString() : null;
                if (!string.Equals(remoteStatus, "COMPLETE", StringComparison.OrdinalIgnoreCase))
                {
                    return TypedResults.Problem($"eSewa reported transaction status: {remoteStatus ?? "unknown"}.", statusCode: StatusCodes.Status400BadRequest);
                }
            }

            await CompletePaymentAsync(db, sub, payment, decoded.TransactionCode, now);
            return TypedResults.Ok(new VerifyPaymentResponse(true, "eSewa payment verified! Your subscription is now active.", await BuildStatusAsync(db, sub, config, env, clock)));
        }

        if (provider == PaymentProvider.Khalti)
        {
            var khalti = ReadKhalti(config);
            if (!khalti.Enabled || string.IsNullOrWhiteSpace(request.KhaltiPidx))
            {
                return Invalid("khaltiPidx", "Missing Khalti payment token (pidx).");
            }

            var payment = await db.PaymentTransactions.SingleOrDefaultAsync(p => p.ProviderRef == request.KhaltiPidx && p.UserId == userId);
            if (payment is null)
            {
                return TypedResults.Problem("Payment record not found.", statusCode: StatusCodes.Status404NotFound);
            }

            if (payment.Status == PaymentStatus.Completed)
            {
                return TypedResults.Ok(new VerifyPaymentResponse(true, "Subscription is already active.", await BuildStatusAsync(db, sub, config, env, clock)));
            }

            var client = httpFactory.CreateClient(GatewayHttpClient);
            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{khalti.BaseUrl}/epayment/lookup/");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Key", khalti.SecretKey);
            msg.Content = JsonContent.Create(new { pidx = request.KhaltiPidx });
            using var resp = await client.SendAsync(msg);
            if (!resp.IsSuccessStatusCode)
            {
                return TypedResults.Problem("Could not verify payment with Khalti.", statusCode: StatusCodes.Status502BadGateway);
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var status = doc.RootElement.TryGetProperty("status", out var sEl) ? sEl.GetString() : null;
            var totalPaisa = doc.RootElement.TryGetProperty("total_amount", out var aEl) && aEl.TryGetInt64(out var pVal) ? pVal : 0L;
            var expectedPaisa = (long)Math.Round(payment.Amount * 100m, MidpointRounding.AwayFromZero);
            if (!string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase) || totalPaisa != expectedPaisa)
            {
                return TypedResults.Problem($"Khalti payment is not completed (status: {status ?? "unknown"}).", statusCode: StatusCodes.Status400BadRequest);
            }

            var txnId = doc.RootElement.TryGetProperty("transaction_id", out var tEl) ? tEl.GetString() : request.KhaltiPidx;
            await CompletePaymentAsync(db, sub, payment, txnId ?? request.KhaltiPidx, now);
            return TypedResults.Ok(new VerifyPaymentResponse(true, "Khalti payment verified! Your subscription is now active.", await BuildStatusAsync(db, sub, config, env, clock)));
        }

        if (provider == PaymentProvider.Stripe)
        {
            var stripe = ReadStripe(config);
            if (!stripe.Enabled || string.IsNullOrWhiteSpace(request.StripeSessionId))
            {
                return Invalid("stripeSessionId", "Missing Stripe checkout session id.");
            }

            var payment = await db.PaymentTransactions.SingleOrDefaultAsync(p => p.ProviderRef == request.StripeSessionId && p.UserId == userId);
            if (payment is null)
            {
                return TypedResults.Problem("Payment record not found.", statusCode: StatusCodes.Status404NotFound);
            }

            if (payment.Status == PaymentStatus.Completed)
            {
                return TypedResults.Ok(new VerifyPaymentResponse(true, "Subscription is already active.", await BuildStatusAsync(db, sub, config, env, clock)));
            }

            var client = httpFactory.CreateClient(GatewayHttpClient);
            using var msg = new HttpRequestMessage(HttpMethod.Get, $"https://api.stripe.com/v1/checkout/sessions/{Uri.EscapeDataString(request.StripeSessionId)}");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stripe.SecretKey);
            using var resp = await client.SendAsync(msg);
            if (!resp.IsSuccessStatusCode)
            {
                return TypedResults.Problem("Could not verify checkout session with Stripe.", statusCode: StatusCodes.Status502BadGateway);
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var payStatus = doc.RootElement.TryGetProperty("payment_status", out var ps) ? ps.GetString() : null;
            var clientRef = doc.RootElement.TryGetProperty("client_reference_id", out var cr) ? cr.GetString() : null;
            if (!string.Equals(payStatus, "paid", StringComparison.OrdinalIgnoreCase) || clientRef != payment.TransactionRef)
            {
                return TypedResults.Problem("Stripe payment is not marked as paid yet.", statusCode: StatusCodes.Status400BadRequest);
            }

            await CompletePaymentAsync(db, sub, payment, request.StripeSessionId, now);
            return TypedResults.Ok(new VerifyPaymentResponse(true, "Card payment verified! Your subscription is now active.", await BuildStatusAsync(db, sub, config, env, clock)));
        }

        return Invalid("provider", "This provider cannot be verified via callback.");
    }

    // ---------------- POST /api/billing/webhooks/stripe ----------------

    private static async Task<IResult> HandleStripeWebhook(
        HttpRequest request, PostgresDbContext db, IConfiguration config, TimeProvider clock)
    {
        var stripe = ReadStripe(config);
        if (string.IsNullOrWhiteSpace(stripe.WebhookSecret))
        {
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        using var reader = new StreamReader(request.Body);
        var payload = await reader.ReadToEndAsync();
        var sigHeader = request.Headers["Stripe-Signature"].ToString();
        var now = clock.GetUtcNow().UtcDateTime;

        if (!PaymentSignatures.VerifyStripeWebhookSignature(payload, sigHeader, stripe.WebhookSecret, now))
        {
            return TypedResults.BadRequest();
        }

        using var doc = JsonDocument.Parse(payload);
        var eventType = doc.RootElement.TryGetProperty("type", out var tEl) ? tEl.GetString() : null;
        if (eventType == "checkout.session.completed"
            && doc.RootElement.TryGetProperty("data", out var data)
            && data.TryGetProperty("object", out var obj))
        {
            var payStatus = obj.TryGetProperty("payment_status", out var ps) ? ps.GetString() : null;
            var clientRef = obj.TryGetProperty("client_reference_id", out var cr) ? cr.GetString() : null;
            var sessionId = obj.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

            if (string.Equals(payStatus, "paid", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(clientRef))
            {
                var payment = await db.PaymentTransactions.SingleOrDefaultAsync(p => p.TransactionRef == clientRef);
                if (payment is { Status: PaymentStatus.Pending })
                {
                    var sub = await db.Subscriptions.SingleAsync(s => s.Id == payment.SubscriptionId);
                    await CompletePaymentAsync(db, sub, payment, sessionId ?? payment.ProviderRef ?? clientRef, now);
                }
            }
        }

        return TypedResults.Ok();
    }

    // ---------------- Internal Helpers ----------------

    private static async Task CompletePaymentAsync(
        PostgresDbContext db, Subscription sub, PaymentTransaction payment, string providerRef, DateTime nowUtc)
    {
        payment.Status = PaymentStatus.Completed;
        payment.ProviderRef = providerRef;
        payment.CompletedAtUtc = nowUtc;
        BillingCatalog.ApplyPaidPlan(sub, payment.Plan, payment.Provider, nowUtc);
        await db.SaveChangesAsync();
    }

    private static PaymentTransaction CreatePendingPayment(
        Guid userId, Guid subscriptionId, BillingPlan plan, PaymentProvider provider,
        decimal amount, string currency, string txRef, DateTime nowUtc) => new()
    {
        UserId = userId,
        SubscriptionId = subscriptionId,
        Plan = plan,
        Provider = provider,
        Status = PaymentStatus.Pending,
        Amount = amount,
        Currency = currency,
        TransactionRef = txRef,
        CreatedAtUtc = nowUtc,
    };

    private static async Task<BillingStatusResponse> BuildStatusAsync(
        PostgresDbContext db, Subscription sub, IConfiguration config, IHostEnvironment env, TimeProvider clock)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var spec = BillingCatalog.SpecFor(sub.Plan);
        var history = await db.PaymentTransactions.AsNoTracking()
            .Where(p => p.UserId == sub.UserId)
            .OrderByDescending(p => p.CreatedAtUtc)
            .Take(25)
            .ToListAsync();

        var providers = new BillingProviderAvailabilityDto(
            ESewa: ReadESewa(config).Enabled,
            Khalti: ReadKhalti(config).Enabled,
            Stripe: ReadStripe(config).Enabled,
            GooglePlay: config.GetValue("Billing:GooglePlay:Enabled", false),
            SandboxTestMode: IsSandboxTestModeEnabled(config, env));

        return new BillingStatusResponse(
            Plan: sub.Plan.ToString(),
            PlanName: spec.Name,
            Status: BillingCatalog.EffectiveStatus(sub, now).ToString(),
            IsActive: BillingCatalog.IsActive(sub, now),
            DaysRemaining: BillingCatalog.DaysRemaining(sub, now),
            CurrentPeriodEndUtc: sub.CurrentPeriodEndUtc,
            MaxCompanies: spec.MaxCompanies,
            MaxStaffPerCompany: spec.MaxStaffPerCompany,
            IncludesStaff: spec.IncludesStaff,
            EnforcementEnabled: IsEnforcementEnabled(config),
            Providers: providers,
            Plans: BillingCatalog.PaidPlans.Select(p => new BillingPlanDto(
                p.Code, p.Name, p.BillingCycle, p.PriceNpr, p.PriceUsd, p.DurationDays,
                p.MaxCompanies, p.MaxStaffPerCompany, p.IncludesStaff, p.IncludesPublicShareLinks)).ToList(),
            History: history.Select(h => new PaymentRecordDto(
                h.Id, h.Plan.ToString(), h.Provider.ToString(), h.Status.ToString(),
                h.Amount, h.Currency, h.TransactionRef, h.ProviderRef, h.CreatedAtUtc, h.CompletedAtUtc)).ToList());
    }

    private static bool IsSandboxTestModeEnabled(IConfiguration config, IHostEnvironment env) =>
        config.GetValue("Billing:AllowSandboxTestMode", env.IsDevelopment());

    private static string ResolveReturnBaseUrl(string? requested, IConfiguration config)
    {
        var configured = config["PublicSite:BaseUrl"]?.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        if (!string.IsNullOrWhiteSpace(requested) && Uri.TryCreate(requested, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return $"{uri.Scheme}://{uri.Authority}";
        }
        return "http://localhost:5121";
    }

    private sealed record ESewaConfig(bool Enabled, string ProductCode, string SecretKey, string FormUrl, string StatusUrl, bool ConfirmStatusRemote);

    private static ESewaConfig ReadESewa(IConfiguration config)
    {
        var productCode = config["Billing:ESewa:ProductCode"] ?? string.Empty;
        var secretKey = config["Billing:ESewa:SecretKey"] ?? string.Empty;
        var enabled = config.GetValue("Billing:ESewa:Enabled", false)
            && !string.IsNullOrWhiteSpace(productCode) && !string.IsNullOrWhiteSpace(secretKey);
        var sandbox = config.GetValue("Billing:ESewa:UseSandbox", true);
        var formUrl = sandbox
            ? "https://rc-epay.esewa.com.np/api/epay/main/v2/form"
            : "https://epay.esewa.com.np/api/epay/main/v2/form";
        var statusUrl = sandbox
            ? "https://rc.esewa.com.np/api/epay/transaction/status/"
            : "https://epay.esewa.com.np/api/epay/transaction/status/";
        var confirmRemote = config.GetValue("Billing:ESewa:ConfirmStatusRemote", true);
        return new(enabled, productCode, secretKey, formUrl, statusUrl, confirmRemote);
    }

    private sealed record KhaltiConfig(bool Enabled, string SecretKey, string BaseUrl);

    private static KhaltiConfig ReadKhalti(IConfiguration config)
    {
        var secretKey = config["Billing:Khalti:SecretKey"] ?? string.Empty;
        var enabled = config.GetValue("Billing:Khalti:Enabled", false) && !string.IsNullOrWhiteSpace(secretKey);
        var sandbox = config.GetValue("Billing:Khalti:UseSandbox", true);
        var baseUrl = sandbox ? "https://dev.khalti.com/api/v2" : "https://khalti.com/api/v2";
        return new(enabled, secretKey, baseUrl);
    }

    private sealed record StripeConfig(bool Enabled, string SecretKey, string WebhookSecret);

    private static StripeConfig ReadStripe(IConfiguration config)
    {
        var secretKey = config["Billing:Stripe:SecretKey"] ?? string.Empty;
        var webhookSecret = config["Billing:Stripe:WebhookSecret"] ?? string.Empty;
        var enabled = config.GetValue("Billing:Stripe:Enabled", false) && !string.IsNullOrWhiteSpace(secretKey);
        return new(enabled, secretKey, webhookSecret);
    }

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
