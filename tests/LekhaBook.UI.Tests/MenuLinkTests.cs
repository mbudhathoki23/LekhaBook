using System.Reflection;
using System.Text.RegularExpressions;
using Bunit;
using LekhaBook.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace LekhaBook.UI.Tests;

/// <summary>Every menu entry opens a screen, and "back to dashboard" really is a link.</summary>
public sealed partial class MenuLinkTests : TestContext
{
    public MenuLinkTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddMudServices();
    }

    [Fact]
    public void Back_to_dashboard_on_a_missing_screen_is_a_link()
    {
        var cut = RenderComponent<NotFound>();

        // With an empty address MudButton renders a button that does nothing when pressed.
        Assert.Equal("/", cut.Find("a.mud-button-root").GetAttribute("href"));
    }

    [Theory]
    [InlineData("Layout/NavMenu.razor")]
    [InlineData("Layout/BottomNav.razor")]
    [InlineData("Pages/Settings.razor")]
    public void Every_menu_address_has_a_screen(string menu)
    {
        var routes = typeof(NotFound).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>())
            .Select(r => r.Template.Trim('/'))
            .ToHashSet();

        var addresses = Href().Matches(File.ReadAllText(Path.Combine(UiFolder(), menu))).Select(m => m.Groups[1].Value.Trim('/')).ToList();

        Assert.NotEmpty(addresses);
        Assert.All(addresses, address => Assert.Contains(address, routes));
    }

    private static string UiFolder([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "LekhaBook.UI");

    [GeneratedRegex("""(?:Href|href)="([a-z/-]*)"(?=[\s/>])""")]
    private static partial Regex Href();
}
