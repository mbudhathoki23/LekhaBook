namespace LekhaBook.App;

public partial class MainPage : ContentPage
{
	public MainPage()
	{
		InitializeComponent();
	}

#if ANDROID
	/// <summary>
	/// Android Back. The answer from the screens comes later, so every press is taken here and the app is
	/// left only when the screens say there is nowhere to go (see BackNavigator).
	/// </summary>
	protected override bool OnBackButtonPressed()
	{
		_ = HandleBackAsync();
		return true;
	}

	private async Task HandleBackAsync()
	{
		var dispatched = await blazorWebView.TryDispatchAsync(services =>
			_ = services.GetRequiredService<LekhaBook.UI.Services.BackNavigator>()
				.HandleAsync(() => MainThread.BeginInvokeOnMainThread(LeaveApp)));
		if (!dispatched)
		{
			LeaveApp(); // the screens have not started yet
		}
	}

	// Like Home: the app stays as it was and comes back to the same screen.
	private static void LeaveApp() => Platform.CurrentActivity?.MoveTaskToBack(true);
#endif
}
