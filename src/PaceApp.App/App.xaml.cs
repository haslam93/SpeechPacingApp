using System.ComponentModel;
using System.Drawing;
using System.Windows;
using PaceApp.Analytics.Services;
using PaceApp.App.Services;
using PaceApp.App.ViewModels;
using PaceApp.Audio.Services;
using PaceApp.Infrastructure.Services;
using DrawingIcon = System.Drawing.Icon;
using DrawingSystemIcons = System.Drawing.SystemIcons;
using Forms = System.Windows.Forms;

namespace PaceApp.App;

public partial class App : System.Windows.Application
{
	private PaceMonitorController? controller;
	private MainWindow? mainWindow;
	private MainWindowViewModel? mainWindowViewModel;
	private Forms.NotifyIcon? notifyIcon;
	private Forms.ToolStripMenuItem? monitoringMenuItem;
	private bool isShutdownRequested;

	protected override async void OnStartup(StartupEventArgs eventArgs)
	{
		base.OnStartup(eventArgs);

		ShutdownMode = ShutdownMode.OnExplicitShutdown;

		controller = new PaceMonitorController(
			new MicrophoneCaptureService(new AudioDeviceWatcher()),
			new SignalPaceMetricsEngine(),
			new JsonAppStateRepository());

		await controller.InitializeAsync();

		mainWindowViewModel = new MainWindowViewModel(controller, new StartupRegistrationService());
		mainWindowViewModel.HideRequested += OnHideRequested;
		mainWindowViewModel.ExitRequested += OnExitRequested;
		mainWindowViewModel.PropertyChanged += OnViewModelPropertyChanged;

		mainWindow = new MainWindow(mainWindowViewModel);
		mainWindow.Closing += OnMainWindowClosing;

		InitializeTrayIcon();

		if (!eventArgs.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
		{
			ShowMainWindow();
		}
	}

	protected override void OnExit(ExitEventArgs eventArgs)
	{
		notifyIcon?.Dispose();
		mainWindowViewModel?.Dispose();
		controller?.Dispose();
		base.OnExit(eventArgs);
	}

	private void InitializeTrayIcon()
	{
		var contextMenu = new Forms.ContextMenuStrip();
		contextMenu.Items.Add(new Forms.ToolStripMenuItem("Open Pace Coach", null, (_, _) => ShowMainWindow()));

		monitoringMenuItem = new Forms.ToolStripMenuItem("Start monitoring", null, async (_, _) => await ToggleMonitoringFromTrayAsync());
		contextMenu.Items.Add(monitoringMenuItem);
		contextMenu.Items.Add(new Forms.ToolStripMenuItem("Hide window", null, (_, _) => HideMainWindow()));
		contextMenu.Items.Add(new Forms.ToolStripSeparator());
		contextMenu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, async (_, _) => await ExitApplicationAsync()));

		notifyIcon = new Forms.NotifyIcon
		{
			Text = "Pace Coach",
			Visible = true,
			Icon = !string.IsNullOrWhiteSpace(Environment.ProcessPath)
				? DrawingIcon.ExtractAssociatedIcon(Environment.ProcessPath) ?? DrawingSystemIcons.Application
				: DrawingSystemIcons.Application,
			ContextMenuStrip = contextMenu,
		};

		notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
		UpdateTrayMenuState();
	}

	private void OnHideRequested(object? sender, EventArgs eventArgs)
	{
		HideMainWindow();
	}

	private async void OnExitRequested(object? sender, EventArgs eventArgs)
	{
		await ExitApplicationAsync();
	}

	private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
	{
		if (eventArgs.PropertyName == nameof(MainWindowViewModel.IsMonitoring))
		{
			UpdateTrayMenuState();
		}
	}

	private void OnMainWindowClosing(object? sender, CancelEventArgs eventArgs)
	{
		if (isShutdownRequested)
		{
			return;
		}

		eventArgs.Cancel = true;
		HideMainWindow();
	}

	private async Task ToggleMonitoringFromTrayAsync()
	{
		if (mainWindowViewModel is null)
		{
			return;
		}

		await mainWindowViewModel.ToggleMonitoringAsync();
		UpdateTrayMenuState();
	}

	private void ShowMainWindow()
	{
		if (mainWindow is null)
		{
			return;
		}

		if (!mainWindow.IsVisible)
		{
			mainWindow.Show();
		}

		if (mainWindow.WindowState == WindowState.Minimized)
		{
			mainWindow.WindowState = WindowState.Normal;
		}

		mainWindow.Topmost = mainWindowViewModel?.AlwaysOnTop ?? true;
		mainWindow.Activate();
	}

	private void HideMainWindow()
	{
		mainWindow?.Hide();
	}

	private async Task ExitApplicationAsync()
	{
		if (isShutdownRequested)
		{
			return;
		}

		isShutdownRequested = true;

		mainWindowViewModel?.AllowCloseWindow();

		if (controller is not null && controller.IsMonitoring)
		{
			await controller.StopMonitoringAsync();
		}

		notifyIcon?.Dispose();
		notifyIcon = null;

		mainWindow?.Close();
		Shutdown();
	}

	private void UpdateTrayMenuState()
	{
		if (monitoringMenuItem is null || mainWindowViewModel is null)
		{
			return;
		}

		monitoringMenuItem.Text = mainWindowViewModel.IsMonitoring ? "Stop monitoring" : "Start monitoring";
	}
}

