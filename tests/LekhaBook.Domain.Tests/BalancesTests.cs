using LekhaBook.Domain;

namespace LekhaBook.Domain.Tests;

public sealed class BalancesTests
{
    private readonly TestBooks _b = new();

    [Fact]
    public void Opening_side_sets_the_sign()
    {
        _b.Customer.OpeningBalance = 500m;
        _b.Customer.OpeningSide = BalanceSide.Debit;   // To Receive
        _b.Supplier.OpeningBalance = 300m;
        _b.Supplier.OpeningSide = BalanceSide.Credit;  // To Give

        Assert.Equal(500m, Balances.Of(_b.Customer, []));
        Assert.Equal(-300m, Balances.Of(_b.Supplier, []));
        Assert.Equal(new PartyBalance(500m, PartyBalanceSide.ToReceive), Balances.OfParty(_b.Customer, []));
        Assert.Equal(new PartyBalance(300m, PartyBalanceSide.ToGive), Balances.OfParty(_b.Supplier, []));
    }

    [Fact]
    public void Payment_in_reduces_receivable_and_increases_cash()
    {
        _b.Customer.OpeningBalance = 1000m;
        var txns = new[] { _b.Txn(TransactionType.PaymentIn, _b.Customer, _b.Cash, 400m) };

        Assert.Equal(600m, Balances.Of(_b.Customer, txns));
        Assert.Equal(400m, Balances.Of(_b.Cash, txns));
    }

    [Fact]
    public void Overpaid_customer_flips_to_To_Give()
    {
        var txns = new[] { _b.Txn(TransactionType.PaymentIn, _b.Customer, _b.Cash, 250m) };

        Assert.Equal(new PartyBalance(250m, PartyBalanceSide.ToGive), Balances.OfParty(_b.Customer, txns));
    }

    [Fact]
    public void Settled_party_has_zero_balance()
    {
        _b.Customer.OpeningBalance = 100m;
        var txns = new[] { _b.Txn(TransactionType.PaymentIn, _b.Customer, _b.Cash, 100m) };

        Assert.Equal(new PartyBalance(0m, PartyBalanceSide.Settled), Balances.OfParty(_b.Customer, txns));
    }

    [Fact]
    public void Void_and_deleted_transactions_are_ignored()
    {
        var voided = _b.Txn(TransactionType.PaymentIn, _b.Customer, _b.Cash, 100m);
        voided.Status = TransactionStatus.Void;
        var deleted = _b.Txn(TransactionType.PaymentIn, _b.Customer, _b.Cash, 200m);
        deleted.IsDeleted = true;
        var live = _b.Txn(TransactionType.PaymentIn, _b.Customer, _b.Cash, 300m);

        Assert.Equal(300m, Balances.Of(_b.Cash, [voided, deleted, live]));
    }

    /// <summary>
    /// A week in a small shop, worked out by hand:
    ///
    /// Openings: Cash 5,000 Dr · Customer 12,000 To Receive · Supplier 8,000 To Give.
    ///
    /// | Day | Entry                                   | Cash   | Bank  | Customer | Supplier | Fuel | Commission | Adjustment |
    /// |-----|-----------------------------------------|--------|-------|----------|----------|------|------------|------------|
    /// |  1  | Payment In 7,000 from Customer to Cash  | +7,000 |       | −7,000   |          |      |            |            |
    /// |  2  | Transfer 6,000 Cash → Bank              | −6,000 | +6,000|          |          |      |            |            |
    /// |  3  | Payment Out 3,500 Bank → Supplier       |        | −3,500|          | +3,500   |      |            |            |
    /// |  3  | Expense 450 Fuel from Cash              | −450   |       |          |          | +450 |            |            |
    /// |  4  | Other Income 1,200 Commission → Cash    | +1,200 |       |          |          |      | −1,200     |            |
    /// |  5  | Party Adjust Add 300 to Customer        |        |       | +300     |          |      |            | −300       |
    /// |  5  | Reduce Money 50 from Cash (count short) | −50    |       |          |          |      |            | +50        |
    ///
    /// Closing: Cash 6,700 · Bank 2,500 · Customer 5,300 To Receive · Supplier 4,500 To Give ·
    /// Fuel 450 · Commission −1,200 · Adjustment −250.
    /// </summary>
    [Fact]
    public void Hand_calculated_week_matches()
    {
        _b.Cash.OpeningBalance = 5000m;
        _b.Customer.OpeningBalance = 12000m;
        _b.Supplier.OpeningBalance = 8000m;
        _b.Supplier.OpeningSide = BalanceSide.Credit;

        var txns = new[]
        {
            _b.Txn(TransactionType.PaymentIn, _b.Customer, _b.Cash, 7000m, day: 1),
            _b.Txn(TransactionType.TransferMoney, _b.Cash, _b.Bank, 6000m, day: 2),
            _b.Txn(TransactionType.PaymentOut, _b.Bank, _b.Supplier, 3500m, day: 3),
            _b.Txn(TransactionType.Expense, _b.Cash, _b.Fuel, 450m, day: 3),
            _b.Txn(TransactionType.OtherIncome, _b.Commission, _b.Cash, 1200m, day: 4),
            _b.Txn(TransactionType.PartyAdjustAdd, _b.Adjustment, _b.Customer, 300m, day: 5),
            _b.Txn(TransactionType.ReduceMoney, _b.Cash, _b.Adjustment, 50m, day: 5),
        };

        foreach (var t in txns)
        {
            var (from, to) = (_b.All.Single(a => a.Id == t.FromAccountId), _b.All.Single(a => a.Id == t.ToAccountId));
            Assert.Empty(PostingRules.Validate(t, from, to));
        }

        var balances = Balances.All(_b.All, txns);

        Assert.Equal(6700m, balances[_b.Cash.Id]);
        Assert.Equal(2500m, balances[_b.Bank.Id]);
        Assert.Equal(5300m, balances[_b.Customer.Id]);
        Assert.Equal(-4500m, balances[_b.Supplier.Id]);
        Assert.Equal(450m, balances[_b.Fuel.Id]);
        Assert.Equal(-1200m, balances[_b.Commission.Id]);
        Assert.Equal(-250m, balances[_b.Adjustment.Id]);
        Assert.Equal(0m, balances[_b.Wallet.Id]);

        // Single-account calculation agrees with the all-accounts pass.
        foreach (var account in _b.All)
        {
            Assert.Equal(balances[account.Id], Balances.Of(account, txns));
        }
    }

    [Fact]
    public void All_balances_plus_opening_equity_sum_to_zero()
    {
        _b.Cash.OpeningBalance = 5000m;
        _b.Customer.OpeningBalance = 12000m;
        _b.Supplier.OpeningBalance = 8000m;
        _b.Supplier.OpeningSide = BalanceSide.Credit;
        var txns = new[]
        {
            _b.Txn(TransactionType.PaymentIn, _b.Customer, _b.Cash, 7000m),
            _b.Txn(TransactionType.Expense, _b.Cash, _b.Fuel, 450.75m),
            _b.Txn(TransactionType.OtherIncome, _b.Commission, _b.Wallet, 99.99m),
            _b.Txn(TransactionType.PartyAdjustReduce, _b.Supplier, _b.Adjustment, 12.5m),
        };

        var total = Balances.All(_b.All, txns).Values.Sum() + Balances.OpeningEquity(_b.All);

        Assert.Equal(0m, total);
        Assert.Equal(-9000m, Balances.OpeningEquity(_b.All));
    }

    [Fact]
    public void Ledger_runs_from_opening_in_date_then_entry_order()
    {
        _b.Customer.OpeningBalance = 1000m;
        var later = _b.Txn(TransactionType.PaymentIn, _b.Customer, _b.Cash, 300m, day: 9);
        var first = _b.Txn(TransactionType.PartyAdjustAdd, _b.Adjustment, _b.Customer, 200m, day: 2);
        var sameDaySecond = _b.Txn(TransactionType.PaymentIn, _b.Customer, _b.Bank, 100m, day: 2);
        var unrelated = _b.Txn(TransactionType.Expense, _b.Cash, _b.Fuel, 50m, day: 1);

        var lines = Balances.Ledger(_b.Customer, [later, sameDaySecond, unrelated, first]);

        Assert.Equal([first, sameDaySecond, later], lines.Select(l => l.Transaction));
        Assert.Equal([1200m, 1100m, 800m], lines.Select(l => l.Balance));
        Assert.Equal(200m, lines[0].Debit);
        Assert.Equal(100m, lines[1].Credit);
        Assert.Equal(Balances.Of(_b.Customer, [later, first, sameDaySecond, unrelated]), lines[^1].Balance);
    }
}
