using System.Text;
using System.Text.Json;
using LekhaBook.Domain;

namespace LekhaBook.Domain.Tests;

public sealed class BillingTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void New_trial_is_active_for_14_days_and_expires_afterward()
    {
        var sub = BillingCatalog.CreateTrial(Guid.CreateVersion7(), Now);

        Assert.True(BillingCatalog.IsActive(sub, Now));
        Assert.Equal(14, BillingCatalog.DaysRemaining(sub, Now));
        Assert.Equal(SubscriptionStatus.Trialing, BillingCatalog.EffectiveStatus(sub, Now));

        var expiredAt = Now.AddDays(14);
        Assert.False(BillingCatalog.IsActive(sub, expiredAt));
        Assert.Equal(0, BillingCatalog.DaysRemaining(sub, expiredAt));
        Assert.Equal(SubscriptionStatus.Expired, BillingCatalog.EffectiveStatus(sub, expiredAt));
        Assert.NotNull(BillingCatalog.CheckCreateCompany(sub, 0, expiredAt));
    }

    [Fact]
    public void Early_renewal_stacks_duration_onto_existing_paid_period()
    {
        var sub = BillingCatalog.CreateTrial(Guid.CreateVersion7(), Now);
        BillingCatalog.ApplyPaidPlan(sub, BillingPlan.ProMonthly, PaymentProvider.ESewa, Now);
        Assert.Equal(Now.AddDays(30), sub.CurrentPeriodEndUtc);

        // Renew 10 days later -> should stack 365 days onto remaining 20 days (total +385 from Now)
        var renewAt = Now.AddDays(10);
        BillingCatalog.ApplyPaidPlan(sub, BillingPlan.BusinessYearly, PaymentProvider.Khalti, renewAt);
        Assert.Equal(BillingPlan.BusinessYearly, sub.Plan);
        Assert.Equal(Now.AddDays(395), sub.CurrentPeriodEndUtc);
        Assert.Null(BillingCatalog.CheckInviteStaff(sub, 5, renewAt));
    }

    [Fact]
    public void Pro_plan_blocks_staff_invites_and_enforces_company_limit()
    {
        var sub = BillingCatalog.CreateTrial(Guid.CreateVersion7(), Now);
        BillingCatalog.ApplyPaidPlan(sub, BillingPlan.ProYearly, PaymentProvider.ESewa, Now);

        Assert.Null(BillingCatalog.CheckCreateCompany(sub, 2, Now));
        Assert.NotNull(BillingCatalog.CheckCreateCompany(sub, 3, Now));
        Assert.NotNull(BillingCatalog.CheckInviteStaff(sub, 1, Now));
    }

    [Fact]
    public void Esewa_v2_hmac_signature_verifies_valid_callback_and_rejects_tampering()
    {
        const string secret = "8gBm/:&EnhH.1/q";
        var fields = "transaction_code,status,total_amount,transaction_uuid,product_code,signed_field_names";
        var msg = $"transaction_code=0007ABC,status=COMPLETE,total_amount=2499,transaction_uuid=LB-20261127-1234,product_code=EPAYTEST,signed_field_names={fields}";
        var sig = PaymentSignatures.ComputeHmacSha256Base64(msg, secret);

        var payload = JsonSerializer.Serialize(new
        {
            transaction_code = "0007ABC",
            status = "COMPLETE",
            total_amount = "2499",
            transaction_uuid = "LB-20261127-1234",
            product_code = "EPAYTEST",
            signed_field_names = fields,
            signature = sig,
        });
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));

        var decoded = PaymentSignatures.ParseAndVerifyESewaCallback(base64, secret);
        Assert.NotNull(decoded);
        Assert.True(decoded!.SignatureValid);
        Assert.Equal("LB-20261127-1234", decoded.TransactionUuid);

        // Tamper amount -> signature must fail!
        var tampered = payload.Replace("2499", "10");
        var tamperedDecoded = PaymentSignatures.ParseAndVerifyESewaCallback(Convert.ToBase64String(Encoding.UTF8.GetBytes(tampered)), secret);
        Assert.NotNull(tamperedDecoded);
        Assert.False(tamperedDecoded!.SignatureValid);
    }

    [Fact]
    public void Stripe_webhook_signature_verifies_fresh_payload_and_rejects_stale_or_tampered()
    {
        const string secret = "whsec_test_secret_123";
        const string body = "{\"type\":\"checkout.session.completed\"}";
        var ts = new DateTimeOffset(Now).ToUnixTimeSeconds();
        var v1 = PaymentSignatures.ComputeHmacSha256Hex($"{ts}.{body}", secret);
        var header = $"t={ts},v1={v1}";

        Assert.True(PaymentSignatures.VerifyStripeWebhookSignature(body, header, secret, Now));
        Assert.False(PaymentSignatures.VerifyStripeWebhookSignature(body + " ", header, secret, Now));
        Assert.False(PaymentSignatures.VerifyStripeWebhookSignature(body, header, secret, Now.AddMinutes(10)));
    }
}
