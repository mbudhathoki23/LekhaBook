namespace LekhaBook.UI.Services;

/// <summary>
/// The screens visited, for the Back button of a phone. Forms are never returned to: Save opens the next
/// screen, so going back to the form would only show it empty again.
/// </summary>
public sealed class BackHistory
{
    private const int Limit = 50;
    private readonly List<string> _paths = [];

    /// <summary>Records the screen now showing, as a path relative to the app root.</summary>
    public void Visit(string path)
    {
        if (_paths.Count > 0 && _paths[^1] == path)
        {
            return;
        }

        if (_paths.Count > 1 && _paths[^2] == path)
        {
            _paths.RemoveAt(_paths.Count - 1); // went back through a link in the app
            return;
        }

        _paths.Add(path);
        if (_paths.Count > Limit)
        {
            _paths.RemoveAt(0);
        }
    }

    /// <summary>The screen Back should open now, or null when Back should leave the app.</summary>
    public string? Back()
    {
        var current = _paths.Count > 0 ? _paths[^1] : "";
        var here = Route(current);
        if (here is "" or "signin")
        {
            return null; // the first screen, signed in or not
        }

        for (var i = _paths.Count - 2; i >= 0; i--)
        {
            var route = Route(_paths[i]);
            var isForm = route.EndsWith("/new", StringComparison.Ordinal) || route.EndsWith("/edit", StringComparison.Ordinal);
            var isSignIn = route is "signin" or "signup";
            if (isForm || (isSignIn && here != "signup") || _paths[i] == current)
            {
                continue;
            }

            var target = _paths[i];
            _paths.RemoveRange(i + 1, _paths.Count - i - 1);
            return target;
        }

        // Nothing to return to, for example a screen opened from a link: go to the first screen.
        _paths.Clear();
        _paths.Add("");
        return "";
    }

    private static string Route(string path) => path.Split('?', '#')[0].Trim('/');
}
