using Bunit;
using LekhaBook.Contracts;
using LekhaBook.UI.Components;
using LekhaBook.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace LekhaBook.UI.Tests;

public sealed class DateFieldTests : TestContext
{
    private static readonly DateTime Mangsir10 = new(2026, 11, 26);

    public DateFieldTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddMudServices();
    }

    [Fact]
    public void A_bs_company_sees_the_miti_with_the_ad_date_under_it()
    {
        var cut = Render("BS", Mangsir10, _ => { });

        Assert.Equal("2083/08/10", cut.Find("input").GetAttribute("value"));
        Assert.Contains("AD 2026-11-26", cut.Markup);
    }

    [Fact]
    public void A_typed_miti_binds_the_ad_date()
    {
        DateTime? value = Mangsir10;
        var cut = Render("BS", value, v => value = v);

        cut.Find("input").Change("2083-8-1");

        Assert.Equal(new DateTime(2026, 11, 17), value);
        Assert.False(cut.Instance.IsInvalid);
    }

    [Fact]
    public void An_unreadable_miti_keeps_the_old_value_and_is_flagged()
    {
        var changed = false;
        var cut = Render("BS", Mangsir10, _ => changed = true);

        cut.Find("input").Change("2083/08/32");

        Assert.False(changed);
        Assert.True(cut.Instance.IsInvalid);
        Assert.Contains("Enter a Nepali date", cut.Markup);
    }

    [Fact]
    public void Clearing_the_field_binds_no_date()
    {
        DateTime? value = Mangsir10;
        var cut = Render("BS", value, v => value = v);

        cut.Find("input").Change("");

        Assert.Null(value);
        Assert.False(cut.Instance.IsInvalid);
    }

    [Fact]
    public void An_ad_company_gets_the_standard_picker()
    {
        var cut = Render("AD", Mangsir10, _ => { });

        Assert.NotNull(cut.FindComponent<MudDatePicker>());
        Assert.Equal("2026-11-26", cut.Find("input").GetAttribute("value"));
    }

    [Fact]
    public void The_miti_calendar_lists_the_month_and_picks_a_day()
    {
        DateOnly? picked = null;
        var cut = RenderComponent<BsCalendar>(p => p
            .Add(c => c.Selected, DateOnly.FromDateTime(Mangsir10))
            .Add(c => c.OnPick, EventCallback.Factory.Create<DateOnly>(this, d => picked = d)));

        var days = cut.FindAll("button.bs-calendar-day");
        Assert.Equal(29, days.Count); // Mangsir 2083
        Assert.Contains("2083/08/10", cut.Find(".bs-calendar-selected").GetAttribute("aria-label"));

        days.First().Click(); // the list's indexer doesn't bind with the pinned AngleSharp

        Assert.Equal(new DateOnly(2026, 11, 17), picked);
    }

    private IRenderedComponent<DateField> Render(string calendar, DateTime? value, Action<DateTime?> changed)
    {
        Services.AddSingleton<ISessionService>(new CompanySession(calendar));
        RenderComponent<MudPopoverProvider>();
        return RenderComponent<DateField>(p => p
            .Add(c => c.Value, value)
            .Add(c => c.ValueChanged, EventCallback.Factory.Create(this, changed)));
    }

    /// <summary>A signed-in user in one company that shows dates in the given calendar.</summary>
    private sealed class CompanySession(string calendar) : ISessionService
    {
        public bool IsInitialized => true;
        public bool IsSignedIn => true;
        public bool IsOffline => false;
        public MeResponse? Me => null;
        public CompanySummary? CurrentCompany { get; } = new(Guid.CreateVersion7(), "Ram Kirana", "Owner", [], calendar);
        public string? UserName => null;
        public string? CompanyName => CurrentCompany?.Name;
        public event EventHandler? Changed { add { } remove { } }
        public Task InitializeAsync() => Task.CompletedTask;
        public Task<SignInOutcome> SignInAsync(string email, string password, string? returnUrl) => throw new NotSupportedException();
        public Task<SignInOutcome> RegisterAsync(string displayName, string email, string password, string? returnUrl) => throw new NotSupportedException();
        public Task SignOutAsync() => Task.CompletedTask;
        public Task SelectCompanyAsync(Guid companyId) => Task.CompletedTask;
        public Task RefreshAsync() => Task.CompletedTask;
        public bool Can(string permission) => true;
    }
}
