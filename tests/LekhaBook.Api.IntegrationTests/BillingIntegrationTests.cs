using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LekhaBook.Contracts;
using LekhaBook.Data.Postgres;
using LekhaBook.Domain;
using LekhaBook.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LekhaBook.Api.IntegrationTests;

/// <summary>
/// Verifies subscription trial provisioning, eSewa ePay v2 signed checkout + idempotent verification,
/// Stripe HMAC webhook activation, and 402 Payment Required plan limit enforcement.
/// </summary>
public sealed class BillingIntegrationTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [PostgresFact]
    public async Task New_online_user_gets_14_day_trial_and_sandbox_checkout_activates_plan()
    {
        var user = await api.NewUserAsync();

        var initial = await user.Http.GetFromJsonAsync<BillingStatusResponse>("/api/billing/status");
        Assert.NotNull(initial);
        Assert.Equal("Trial", initial!.Plan);
        Assert.Equal("Trialing", initial.Status);
        Assert.True(initial.IsActive);
        Assert.Equal(14, initial.DaysRemaining);
        Assert.True(initial.Providers.SandboxTestMode);

        // Activate ProYearly via sandbox checkout
        var checkoutResp = await user.Http.PostAsJsonAsync("/api/billing/checkout", new StartCheckoutRequest("ProYearly", "Manual"));
        Assert.Equal(HttpStatusCode.OK, checkoutResp.StatusCode);
        var checkout = (await checkoutResp.Content.ReadFromJsonAsync<StartCheckoutResponse>())!;
        Assert.True(checkout.CompletedImmediately);

        var after = await user.Http.GetFromJsonAsync<BillingStatusResponse>("/api/billing/status");
        Assert.Equal("ProYearly", after!.Plan);
        Assert.Equal("Active", after.Status);
        Assert.True(after.DaysRemaining >= 364);
        Assert.Single(after.History);
        Assert.Equal("Completed", after.History[0].Status);
    }

    [PostgresFact]
    public async Task Esewa_checkout_and_signed_callback_activate_idempotently_and_enforce_plan_limits()
    {
        const string esewaSecret = "test_esewa_secret_key_for_ci";
        const string stripeWebhookSecret = "whsec_test_ci_secret";

        await using var billingApp = new WebApplicationFactory<Program>().WithTestSettings(new()
        {
            ["ConnectionStrings:LekhaBook"] = api.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("LekhaBook"),
            ["Database:MigrateOnStartup"] = "false",
            ["Billing:EnforceLimits"] = "true",
            ["Billing:AllowSandboxTestMode"] = "false",
            ["Billing:ESewa:Enabled"] = "true",
            ["Billing:ESewa:UseSandbox"] = "true",
            ["Billing:ESewa:ProductCode"] = "EPAYTEST",
            ["Billing:ESewa:SecretKey"] = esewaSecret,
            ["Billing:ESewa:ConfirmStatusRemote"] = "false",
            ["Billing:Stripe:WebhookSecret"] = stripeWebhookSecret,
        });

        var user = await api.NewUserAsync();
        using var client = billingApp.CreateClient();
        client.DefaultRequestHeaders.Authorization = user.Http.DefaultRequestHeaders.Authorization;

        // 1. Sandbox test checkout is rejected when AllowSandboxTestMode=false
        var deniedSandbox = await client.PostAsJsonAsync("/api/billing/checkout", new StartCheckoutRequest("ProMonthly", "Manual"));
        Assert.Equal(HttpStatusCode.Forbidden, deniedSandbox.StatusCode);

        // 2. Start eSewa checkout for ProMonthly (Rs. 299)
        var startResp = await client.PostAsJsonAsync("/api/billing/checkout", new StartCheckoutRequest("ProMonthly", "ESewa", "https://app.lekhabooks.com"));
        Assert.Equal(HttpStatusCode.OK, startResp.StatusCode);
        var start = (await startResp.Content.ReadFromJsonAsync<StartCheckoutResponse>())!;
        Assert.NotNull(start.FormActionUrl);
        Assert.NotNull(start.FormFields);
        Assert.Equal("299", start.FormFields!["total_amount"]);
        Assert.Equal("EPAYTEST", start.FormFields["product_code"]);

        // 3. Simulate eSewa signed callback returning COMPLETE
        const string fields = "transaction_code,status,total_amount,transaction_uuid,product_code,signed_field_names";
        var msg = $"transaction_code=ESEWA999,status=COMPLETE,total_amount=299,transaction_uuid={start.TransactionRef},product_code=EPAYTEST,signed_field_names={fields}";
        var sig = PaymentSignatures.ComputeHmacSha256Base64(msg, esewaSecret);
        var esewaPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            transaction_code = "ESEWA999",
            status = "COMPLETE",
            total_amount = "299",
            transaction_uuid = start.TransactionRef,
            product_code = "EPAYTEST",
            signed_field_names = fields,
            signature = sig,
        })));

        var verify1 = await client.PostAsJsonAsync("/api/billing/verify", new VerifyPaymentRequest("ESewa", ESewaData: esewaPayload));
        Assert.Equal(HttpStatusCode.OK, verify1.StatusCode);
        var verified1 = (await verify1.Content.ReadFromJsonAsync<VerifyPaymentResponse>())!;
        Assert.True(verified1.Activated);
        Assert.Equal("ProMonthly", verified1.Status.Plan);
        var firstExpiry = verified1.Status.CurrentPeriodEndUtc;

        // 4. Re-verifying the exact same eSewa callback is idempotent (never double-extends!)
        var verify2 = await client.PostAsJsonAsync("/api/billing/verify", new VerifyPaymentRequest("ESewa", ESewaData: esewaPayload));
        Assert.Equal(HttpStatusCode.OK, verify2.StatusCode);
        var verified2 = (await verify2.Content.ReadFromJsonAsync<VerifyPaymentResponse>())!;
        Assert.Equal(firstExpiry, verified2.Status.CurrentPeriodEndUtc, TimeSpan.FromMilliseconds(1));

        // 5. Pro plan enforcement: creating a company succeeds, inviting staff returns 402 Payment Required!
        var coResp = await client.PostAsJsonAsync("/api/companies", new CreateCompanyRequest("Pro Shop"));
        Assert.Equal(HttpStatusCode.Created, coResp.StatusCode);
        var co = (await coResp.Content.ReadFromJsonAsync<CompanySummary>())!;

        var inviteBlocked = await client.PostAsJsonAsync($"/api/companies/{co.CompanyId}/invitations",
            new InviteRequest("staff@example.test", "Staff", "EntryPerson"));
        Assert.Equal(HttpStatusCode.PaymentRequired, inviteBlocked.StatusCode);

        // 6. Stripe webhook upgrades user to BusinessYearly -> inviting staff now succeeds!
        var me = (await client.GetFromJsonAsync<MeResponse>("/api/me"))!;
        string stripeRef;
        using (var scope = billingApp.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();
            var sub = await db.Subscriptions.SingleAsync(s => s.UserId == me.UserId);
            stripeRef = $"LB-STRIPE-{Guid.CreateVersion7():N}"[..32];
            db.PaymentTransactions.Add(new PaymentTransaction
            {
                UserId = me.UserId,
                SubscriptionId = sub.Id,
                Plan = BillingPlan.BusinessYearly,
                Provider = PaymentProvider.Stripe,
                Status = PaymentStatus.Pending,
                Amount = 99m,
                Currency = "USD",
                TransactionRef = stripeRef,
                ProviderRef = "cs_test_123",
                CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var webhookBody = JsonSerializer.Serialize(new
        {
            type = "checkout.session.completed",
            data = new { @object = new { id = "cs_test_123", payment_status = "paid", client_reference_id = stripeRef } },
        });
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var v1 = PaymentSignatures.ComputeHmacSha256Hex($"{ts}.{webhookBody}", stripeWebhookSecret);
        using var webhookClient = billingApp.CreateClient();
        using var webhookReq = new HttpRequestMessage(HttpMethod.Post, "/api/billing/webhooks/stripe")
        {
            Content = new StringContent(webhookBody, Encoding.UTF8, "application/json"),
        };
        webhookReq.Headers.Add("Stripe-Signature", $"t={ts},v1={v1}");
        var webhookResp = await webhookClient.SendAsync(webhookReq);
        Assert.Equal(HttpStatusCode.OK, webhookResp.StatusCode);

        var inviteAllowed = await client.PostAsJsonAsync($"/api/companies/{co.CompanyId}/invitations",
            new InviteRequest("staff@example.test", "Staff", "EntryPerson"));
        Assert.Equal(HttpStatusCode.OK, inviteAllowed.StatusCode);
    }
}
