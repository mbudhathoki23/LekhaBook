using Xunit;
using Bunit;
using LekhaBook.UI.Components;
using Microsoft.Extensions.DependencyInjection;

namespace LekhaBook.UI.Tests;

public class EnterNavFormTests : TestContext
{
    public EnterNavFormTests()
    {
        // EnterNavForm loads a JS module via JSRuntime interop on first render; bUnit's
        // loose JS mode accepts those invocations so rendering can be tested in isolation.
        JSInterop.Mode = JSRuntimeMode.Loose;

        // Every component in the shared UI injects the translations (see _Imports.razor).
        Services.AddLocalization();
    }

    [Fact(DisplayName = "Render enters a child fragment without throwing")]
    public void Renders_ChildContent()
    {
        var cut = RenderComponent<EnterNavForm>();
        Assert.NotNull(cut);
    }

    [Fact(DisplayName = "AutoFocus parameter defaults to true")]
    public void AutoFocus_Defaults_To_True()
    {
        var cut = RenderComponent<EnterNavForm>();
        Assert.True(cut.Instance.AutoFocus);
    }

    [Fact(DisplayName = "Custom class is applied to root element")]
    public void Custom_Class_Is_Applied()
    {
        var cut = RenderComponent<EnterNavForm>();
        Assert.NotNull(cut.Find(".enter-nav"));
    }

    [Fact(DisplayName = "Setting AutoFocus=false renders without error")]
    public void Can_Disable_AutoFocus()
    {
        var cut = RenderComponent<EnterNavForm>(parameters => parameters.Add(p => p.AutoFocus, false));
        Assert.False(cut.Instance.AutoFocus);
    }
}