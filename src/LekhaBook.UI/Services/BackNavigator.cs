using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace LekhaBook.UI.Services;

/// <summary>
/// What the Back button of a phone does inside the app: first close an open popup or dialog, otherwise open
/// the previous screen, and leave the app only from the first screen. A form is left the way Cancel leaves
/// it: what was typed is discarded.
/// <para>
/// The router injects this so that screens are recorded from the start. The browser has its own Back button
/// and does not use it.
/// </para>
/// </summary>
public sealed class BackNavigator : IDisposable
{
    private const string Module = "./_content/LekhaBook.UI/js/backButton.js";

    private readonly NavigationManager _navigation;
    private readonly IJSRuntime _js;
    private readonly BackHistory _history = new();
    private IJSObjectReference? _module;
    private bool _busy;

    public BackNavigator(NavigationManager navigation, IJSRuntime js)
    {
        _navigation = navigation;
        _js = js;
        _history.Visit(navigation.ToBaseRelativePath(navigation.Uri));
        navigation.LocationChanged += OnLocationChanged;
    }

    /// <summary>Handles one press of Back. Calls <paramref name="leaveApp"/> when there is nowhere to go.</summary>
    public async Task HandleAsync(Action leaveApp)
    {
        if (_busy)
        {
            return; // a second press while the first is still being handled
        }

        _busy = true;
        try
        {
            _module ??= await _js.InvokeAsync<IJSObjectReference>("import", Module);
            if (await _module.InvokeAsync<bool>("closeTopPopup"))
            {
                return;
            }

            if (_history.Back() is { } target)
            {
                _navigation.NavigateTo(target, replace: true);
            }
            else
            {
                leaveApp();
            }
        }
        catch (JSException)
        {
            leaveApp(); // never leave the button doing nothing
        }
        finally
        {
            _busy = false;
        }
    }

    public void Dispose() => _navigation.LocationChanged -= OnLocationChanged;

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e) =>
        _history.Visit(_navigation.ToBaseRelativePath(e.Location));
}
