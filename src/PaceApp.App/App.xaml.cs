using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
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
	private const string SingleInstanceMutexName = "PaceApp.SingleInstance";
	private const string SingleInstancePipeName = "PaceApp.SingleInstancePipe";

	private PaceMonitorController? controller;
	private AppDiagnosticsService? diagnosticsService;
	private MainWindow? mainWindow;
	private MainWindowViewModel? mainWindowViewModel;
	private Forms.NotifyIcon? notifyIcon;
	private Forms.ToolStripMenuItem? monitoringMenuItem;
	private Mutex? singleInstanceMutex;
	private CancellationTokenSource? singleInstanceCancellationSource;
	private Task? singleInstanceListenerTask;
	private bool isShutdownRequested;

	protected override void OnStartup(StartupEventArgs eventArgs)
	{
		base.OnStartup(eventArgs);
		diagnosticsService = new AppDiagnosticsService();
		diagnosticsService.Write($"Startup requested. Args: {string.Join(' ', eventArgs.Args)}");
		DispatcherUnhandledException += OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

		if (!AcquireSingleInstance(eventArgs.Args))
		{
			diagnosticsService.Write("Another PaceApp instance is already active. Exiting this launch request.");
			Shutdown();
			return;
		}

		try
		{
			ShutdownMode = ShutdownMode.OnExplicitShutdown;

			controller = new PaceMonitorController(
				new MicrophoneCaptureService(new AudioDeviceWatcher()),
				new SignalPaceMetricsEngine(),
				new JsonAppStateRepository());

			controller.InitializeAsync().GetAwaiter().GetResult();
			diagnosticsService.Write("Application services initialized.");

			mainWindowViewModel = new MainWindowViewModel(controller, new StartupRegistrationService(), diagnosticsService);
			mainWindowViewModel.HideRequested += OnHideRequested;
			mainWindowViewModel.ExitRequested += OnExitRequested;
			mainWindowViewModel.PropertyChanged += OnViewModelPropertyChanged;

			mainWindow = new MainWindow(mainWindowViewModel);
			MainWindow = mainWindow;
			mainWindow.Closing += OnMainWindowClosing;

			InitializeTrayIcon();
			diagnosticsService.Write("Tray icon initialized.");

			var startHiddenInTray = eventArgs.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
			mainWindowViewModel.SetLaunchMode(startHiddenInTray ? "Tray startup" : "Visible startup");
			mainWindowViewModel.AppendDiagnostic(startHiddenInTray
				? "App started in tray mode. Use the tray icon to reopen the window."
				: "App started in visible mode.");

			if (!startHiddenInTray)
			{
				ShowMainWindow();
			}
			else
			{
				mainWindow.Hide();
			}
		}
		catch (Exception exception)
		{
			diagnosticsService.Write($"Startup failure: {exception}");
			System.Windows.MessageBox.Show(
				$"PaceApp failed to start.\n\n{exception.Message}\n\nDiagnostics: {diagnosticsService.LogFilePath}",
				"PaceApp startup error",
				System.Windows.MessageBoxButton.OK,
				System.Windows.MessageBoxImage.Error);
			Shutdown();
		}
	}

	protected override void OnExit(ExitEventArgs eventArgs)
	{
		DispatcherUnhandledException -= OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
		singleInstanceCancellationSource?.Cancel();
		singleInstanceMutex?.ReleaseMutex();
		singleInstanceMutex?.Dispose();
		notifyIcon?.Dispose();
		mainWindowViewModel?.Dispose();
		controller?.Dispose();
		base.OnExit(eventArgs);
	}

	private bool AcquireSingleInstance(string[] args)
	{
		singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
		if (createdNew)
		{
			diagnosticsService?.Write("Single-instance mutex acquired.");
			singleInstanceCancellationSource = new CancellationTokenSource();
			singleInstanceListenerTask = Task.Run(() => ListenForSingleInstanceSignalsAsync(singleInstanceCancellationSource.Token));
			return true;
		}

		var shouldShowExistingWindow = !args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
		if (shouldShowExistingWindow)
		{
			SignalExistingInstance("show");
		}

		singleInstanceMutex.Dispose();
		singleInstanceMutex = null;
		return false;
	}

	private async Task ListenForSingleInstanceSignalsAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				using var pipe = new NamedPipeServerStream(
					SingleInstancePipeName,
					PipeDirection.In,
					1,
					PipeTransmissionMode.Byte,
					PipeOptions.Asynchronous);

				await pipe.WaitForConnectionAsync(cancellationToken);
				using var reader = new StreamReader(pipe, Encoding.UTF8);
				var command = await reader.ReadLineAsync(cancellationToken);
				if (string.Equals(command, "show", StringComparison.OrdinalIgnoreCase))
				{
					diagnosticsService?.Write("Received show command from a second launch request.");
					await Dispatcher.InvokeAsync(ShowMainWindow);
				}
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch
			{
				await Task.Delay(250, cancellationToken);
			}
		}
	}

	private void SignalExistingInstance(string command)
	{
		try
		{
			using var pipe = new NamedPipeClientStream(".", SingleInstancePipeName, PipeDirection.Out);
			pipe.Connect(750);
			using var writer = new StreamWriter(pipe, Encoding.UTF8)
			{
				AutoFlush = true,
			};

			writer.WriteLine(command);
			diagnosticsService?.Write("Sent show command to the existing PaceApp instance.");
		}
		catch
		{
			diagnosticsService?.Write("Failed to signal the existing PaceApp instance.");
		}
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
		mainWindow.ShowInTaskbar = true;
		mainWindow.Activate();
		diagnosticsService?.Write("Main window shown.");
		mainWindowViewModel?.AppendDiagnostic("Main window shown.");
	}

	private void HideMainWindow()
	{
		mainWindow?.Hide();
		diagnosticsService?.Write("Main window hidden to tray.");
		mainWindowViewModel?.AppendDiagnostic("Main window hidden to tray.");
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

	private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs eventArgs)
	{
		diagnosticsService?.Write($"Dispatcher exception: {eventArgs.Exception}");
		mainWindowViewModel?.AppendDiagnostic($"UI exception: {eventArgs.Exception.Message}");
	}

	private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs eventArgs)
	{
		diagnosticsService?.Write($"Unhandled exception: {eventArgs.ExceptionObject}");
	}
}

