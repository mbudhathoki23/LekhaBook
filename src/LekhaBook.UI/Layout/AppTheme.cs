using MudBlazor;

namespace LekhaBook.UI.Layout;

/// <summary>LekhaBook colours and type. Our own palette, not the reference app's.</summary>
public static class AppTheme
{
    public static MudTheme Theme { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#3949AB",
            Secondary = "#00897B",
            AppbarBackground = "#3949AB",
            Background = "#F5F6FA",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#7986CB",
            Secondary = "#4DB6AC",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
        },
    };
}
