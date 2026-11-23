using LekhaBook.Domain;

namespace LekhaBook.Domain.Tests;

public sealed class PostingRulesTests
{
    private readonly TestBooks _b = new();

    public static TheoryData<TransactionType, string, string> ValidPairs => new()
    {
        { TransactionType.PaymentIn, "Customer", "Cash" },
        { TransactionType.PaymentIn, "Supplier", "Bank" },
        { TransactionType.PaymentOut, "Cash", "Supplier" },
        { TransactionType.PaymentOut, "Wallet", "Customer" },
        { TransactionType.PartyAdjustAdd, "Adjustment", "Customer" },
        { TransactionType.PartyAdjustReduce, "Customer", "Adjustment" },
        { TransactionType.Expense, "Cash", "Fuel" },
        { TransactionType.OtherIncome, "Commission", "Bank" },
        { TransactionType.AddMoney, "Adjustment", "Cash" },
        { TransactionType.ReduceMoney, "Wallet", "Adjustment" },
        { TransactionType.TransferMoney, "Cash", "Bank" },
        { TransactionType.TransferMoney, "Bank", "Wallet" },
    };

    public static TheoryData<TransactionType, string, string> InvalidPairs => new()
    {
        // Wrong direction
        { TransactionType.PaymentIn, "Cash", "Customer" },
        { TransactionType.PaymentOut, "Supplier", "Cash" },
        { TransactionType.Expense, "Fuel", "Cash" },
        { TransactionType.OtherIncome, "Bank", "Commission" },
        // Wrong account kinds
        { TransactionType.PaymentIn, "Customer", "Supplier" },
        { TransactionType.PaymentIn, "Commission", "Cash" },
        { TransactionType.Expense, "Customer", "Fuel" },
        { TransactionType.Expense, "Cash", "Commission" },
        { TransactionType.TransferMoney, "Cash", "Customer" },
        { TransactionType.TransferMoney, "Fuel", "Bank" },
        { TransactionType.AddMoney, "Customer", "Cash" },
        { TransactionType.PartyAdjustAdd, "Cash", "Customer" },
    };

    [Theory]
    [MemberData(nameof(ValidPairs))]
    public void Allowed_account_pairs_are_valid(TransactionType type, string from, string to)
    {
        var (f, t) = (Get(from), Get(to));

        var errors = PostingRules.Validate(_b.Txn(type, f, t, 100m), f, t);

        Assert.Empty(errors);
    }

    [Theory]
    [MemberData(nameof(InvalidPairs))]
    public void Wrong_account_pairs_are_rejected(TransactionType type, string from, string to)
    {
        var (f, t) = (Get(from), Get(to));

        var errors = PostingRules.Validate(_b.Txn(type, f, t, 100m), f, t);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Every_transaction_type_has_a_rule()
    {
        foreach (var type in Enum.GetValues<TransactionType>())
        {
            _ = PostingRules.RolesFor(type); // throws if missing
        }
    }

    [Fact]
    public void Transfer_to_the_same_account_is_rejected()
    {
        var errors = PostingRules.Validate(_b.Txn(TransactionType.TransferMoney, _b.Cash, _b.Cash, 50m), _b.Cash, _b.Cash);

        Assert.Contains(errors, e => e.Contains("different accounts"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-10")]
    [InlineData("10.005")]
    [InlineData("10000000000000000")]
    public void Invalid_amounts_are_rejected(string amount)
    {
        var t = _b.Txn(TransactionType.PaymentIn, _b.Customer, _b.Cash, decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture));

        var errors = PostingRules.Validate(t, _b.Customer, _b.Cash);

        Assert.Contains(errors, e => e.StartsWith("Amount"));
    }

    [Theory]
    [InlineData("0.01")]
    [InlineData("10.5")]
    [InlineData("9999999999999999.99")]
    public void Valid_amounts_pass(string amount) =>
        Assert.True(PostingRules.IsValidAmount(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Account_from_another_company_is_rejected()
    {
        var otherCompanyCash = new TestBooks().Cash;
        var t = _b.Txn(TransactionType.PaymentIn, _b.Customer, otherCompanyCash, 10m);

        var errors = PostingRules.Validate(t, _b.Customer, otherCompanyCash);

        Assert.Contains(errors, e => e.Contains("company"));
    }

    [Fact]
    public void Deleted_account_is_rejected()
    {
        _b.Customer.IsDeleted = true;
        var t = _b.Txn(TransactionType.PaymentIn, _b.Customer, _b.Cash, 10m);

        var errors = PostingRules.Validate(t, _b.Customer, _b.Cash);

        Assert.Contains(errors, e => e.Contains("deleted"));
    }

    private Account Get(string name) => name switch
    {
        "Customer" => _b.Customer,
        "Supplier" => _b.Supplier,
        "Cash" => _b.Cash,
        "Bank" => _b.Bank,
        "Wallet" => _b.Wallet,
        "Fuel" => _b.Fuel,
        "Commission" => _b.Commission,
        "Adjustment" => _b.Adjustment,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}
