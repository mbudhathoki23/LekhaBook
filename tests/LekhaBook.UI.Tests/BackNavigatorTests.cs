using Bunit;
using Bunit.TestDoubles;
using LekhaBook.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace LekhaBook.UI.Tests;

public sealed class BackNavigatorTests : Bunit.TestContext
{
    private int _left;

    private (BackNavigator Back, FakeNavigationManager Navigation) Start(Action<BunitJSModuleInterop>? script = null)
    {
        var module = JSInterop.SetupModule("./_content/LekhaBook.UI/js/backButton.js");
        if (script is null)
        {
            module.Setup<bool>("closeTopPopup").SetResult(false);
        }
        else
        {
            script(module);
        }

        Services.AddScoped<BackNavigator>();
        var back = Services.GetRequiredService<BackNavigator>();
        return (back, Services.GetRequiredService<FakeNavigationManager>());
    }

    private Task Press(BackNavigator back) => back.HandleAsync(() => _left++);

    private static string Screen(FakeNavigationManager navigation) => navigation.ToBaseRelativePath(navigation.Uri);

    [Fact]
    public async Task Opens_the_previous_screen_then_leaves_from_the_first_one()
    {
        var (back, navigation) = Start();
        navigation.NavigateTo("parties");
        navigation.NavigateTo("parties/new");

        await Press(back);
        Assert.Equal("parties", Screen(navigation));
        await Press(back);
        Assert.Equal("", Screen(navigation));
        Assert.Equal(0, _left);

        await Press(back);
        Assert.Equal(1, _left);
    }

    [Fact]
    public async Task An_open_popup_takes_the_press_and_the_screen_stays()
    {
        var (back, navigation) = Start(m => m.Setup<bool>("closeTopPopup").SetResult(true));
        navigation.NavigateTo("parties");

        await Press(back);

        Assert.Equal("parties", Screen(navigation));
        Assert.Equal(0, _left);
    }

    [Fact]
    public async Task Leaves_the_app_when_the_screen_cannot_be_asked()
    {
        var (back, navigation) = Start(m => m.Setup<bool>("closeTopPopup").SetException(new JSException("gone")));
        navigation.NavigateTo("parties");

        await Press(back);

        Assert.Equal(1, _left);
    }
}
