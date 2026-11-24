namespace LekhaBook.App;

// Fully qualified: inside namespace LekhaBook.App, a bare "Application" would mean the LekhaBook.Application namespace.
public partial class App : Microsoft.Maui.Controls.Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new MainPage()) { Title = "Lekha Books" };
	}
}
