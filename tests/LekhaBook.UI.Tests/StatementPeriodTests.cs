using LekhaBook.Contracts;
using LekhaBook.UI.Components;

namespace LekhaBook.UI.Tests;

/// <summary>The period printed on a statement covers every dated row, wherever the opening date falls.</summary>
public sealed class StatementPeriodTests
{
    private static DateOnly Day(int day) => new(2026, 11, day);

    [Fact]
    public void An_entry_before_the_opening_date_is_inside_the_period()
    {
        var period = StatementHeader.PeriodOf(Ledger(openingAsOf: Day(24), Day(17), Day(26)));

        Assert.Equal((Day(17), Day(26)), period);
    }

    [Fact]
    public void The_period_does_not_run_backwards_when_every_entry_predates_the_opening_date()
    {
        var period = StatementHeader.PeriodOf(Ledger(openingAsOf: Day(24), Day(17)));

        Assert.Equal((Day(17), Day(24)), period);
    }

    [Fact]
    public void Without_an_opening_date_the_entries_set_both_ends()
    {
        var period = StatementHeader.PeriodOf(Ledger(openingAsOf: null, Day(20), Day(22)));

        Assert.Equal((Day(20), Day(22)), period);
    }

    [Fact]
    public void A_statement_with_no_entries_has_no_period() =>
        Assert.Null(StatementHeader.PeriodOf(Ledger(openingAsOf: Day(24))));

    private static LedgerDto Ledger(DateOnly? openingAsOf, params DateOnly[] dates) => new(
        Guid.CreateVersion7(),
        0,
        openingAsOf,
        dates.Select(d => new LedgerLineDto(Guid.CreateVersion7(), "PaymentIn", "1", d, 0, 10, -10, null, null, null, [])).ToList(),
        0);
}
