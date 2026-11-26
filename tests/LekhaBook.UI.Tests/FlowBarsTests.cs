using Bunit;
using LekhaBook.Application;
using LekhaBook.UI.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace LekhaBook.UI.Tests;

public sealed class FlowBarsTests : TestContext
{
    public FlowBarsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddMudServices();
    }

    [Fact]
    public void Every_bar_in_the_list_shares_the_largest_figure_as_its_full_width()
    {
        var cut = RenderComponent<FlowBars>(p => p.Add(x => x.Rows, [
            ("Shrawan", new FlowTotals(100m, 50m)),
            ("Bhadra", new FlowTotals(200m, 0m)),
        ]));

        Assert.Equal(["width: 50%", "width: 100%"], cut.FindAll(".flow-bar.in").Select(b => b.GetAttribute("style")));
        Assert.Equal(["width: 25%", "width: 0%"], cut.FindAll(".flow-bar.out").Select(b => b.GetAttribute("style")));
    }

    [Fact]
    public void The_amounts_are_written_beside_the_bars_so_nothing_depends_on_colour()
    {
        var cut = RenderComponent<FlowBars>(p => p.Add(x => x.Rows, [("Shrawan", new FlowTotals(200m, 50m))]));

        Assert.Equal(["Rs. 200.00", "Rs. 50.00"], cut.FindAll(".flow-value").Select(v => v.TextContent));
        Assert.Contains("Rs. 150.00", cut.Find(".flow-net").TextContent);
    }

    [Fact]
    public void An_empty_list_draws_the_legend_and_no_bars()
    {
        var cut = RenderComponent<FlowBars>(p => p.Add(x => x.Rows, []));

        Assert.Empty(cut.FindAll(".flow-bar"));
        Assert.Contains("Money in", cut.Markup);
    }
}
