namespace LekhaBook.App;

// Fully qualified: inside namespace LekhaBook.App, a bare "Application" would mean the LekhaBook.Application namespace.
public partial class App : Microsoft.Maui.Controls.Application
{
	public App()
	{
		InitializeComponent();

		// The saved language (English by default) is applied before the first screen is built.
		LekhaBook.UI.Localization.Languages.Apply(
			Microsoft.Maui.Storage.Preferences.Default.Get<string?>(LekhaBook.UI.Localization.Languages.PreferenceKey, null));
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new MainPage()) { Title = "Lekha Books" };
	}
}
