using LekhaBook.Application;
using LekhaBook.Contracts;
using LekhaBook.Domain;
using LekhaBook.UI.Formatting;

namespace LekhaBook.Application.Tests;

public sealed class MasterValidationTests
{
    [Theory]
    [InlineData("0", "Rs. 0.00")]
    [InlineData("999.5", "Rs. 999.50")]
    [InlineData("1000", "Rs. 1,000.00")]
    [InlineData("100000", "Rs. 1,00,000.00")]
    [InlineData("1234567.5", "Rs. 12,34,567.50")]
    [InlineData("-125000.25", "-Rs. 1,25,000.25")]
    public void Money_uses_rupees_and_lakh_grouping(string amount, string expected) =>
        Assert.Equal(expected, Money.Format(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void A_complete_party_is_valid()
    {
        var errors = MasterValidation.Validate(new SavePartyRequest(
            "Sita Hardware", "Customer", "+977 984-1000001", "sita@example.test", "New Road", "301234567",
            OpeningBalance: 2500.50m, OpeningSide: "ToGive"));

        Assert.Empty(errors);
    }

    [Fact]
    public void Party_fields_are_checked()
    {
        var errors = MasterValidation.Validate(new SavePartyRequest(
            "", "Vendor", Phone: "call me", Email: "nope", Pan: "AB12", OpeningBalance: 10.005m, OpeningSide: "Later"));

        Assert.Equal(["email", "name", "openingBalance", "openingSide", "pan", "partyType", "phone"], errors.Keys.Order());
    }

    [Fact]
    public void Negative_party_opening_is_rejected_because_the_side_carries_the_sign()
    {
        Assert.Contains("openingBalance", MasterValidation.Validate(new SavePartyRequest("A", "Customer", OpeningBalance: -5)).Keys);
    }

    [Fact]
    public void Money_accounts_allow_an_overdrawn_opening_but_cheque_only_as_the_built_in_account()
    {
        Assert.Empty(MasterValidation.Validate(new SaveMoneyAccountRequest("Nabil Bank", "Bank", OpeningBalance: -1000), isNew: true));
        Assert.Empty(MasterValidation.Validate(new SaveMoneyAccountRequest("Petty Cash", "Cash"), isNew: true));
        Assert.Contains("type", MasterValidation.Validate(new SaveMoneyAccountRequest("Cheque 2", "Cheque"), isNew: true).Keys);
        Assert.Empty(MasterValidation.Validate(new SaveMoneyAccountRequest("Cheque", "Cheque"), isNew: false));
        Assert.Contains("type", MasterValidation.Validate(new SaveMoneyAccountRequest("Fuel", "Expense"), isNew: false).Keys);
    }

    [Theory]
    [InlineData("ToReceive", BalanceSide.Debit)]
    [InlineData("togive", BalanceSide.Credit)]
    public void Opening_sides_map_to_debit_and_credit(string side, BalanceSide expected)
    {
        Assert.Equal(expected, MasterValidation.ParseOpeningSide(side));
        Assert.Equal(side, MasterValidation.OpeningSideName(expected), ignoreCase: true);
    }
}
