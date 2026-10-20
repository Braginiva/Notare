using Microsoft.Extensions.DependencyInjection;

namespace Diary.Mobile;

public partial class App : Application
{
	// Срабатывает, когда приложение возвращают из фона.
	// В это время надо сразу синкнуться: таймеры в фоне Android почти не тикают,
	// а сигнал WebSocket мог прийти, пока приложение было закрыто экраном.
	public static event EventHandler? Resumed;

	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}

	protected override void OnResume()
	{
		base.OnResume();
		Resumed?.Invoke(this, EventArgs.Empty);
	}
}
