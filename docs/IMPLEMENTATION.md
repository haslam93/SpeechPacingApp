---
title: PaceApp implementation notes
description: Detailed record of what was created during the first PaceApp implementation pass, including project structure, design choices, and validation work
author: GitHub Copilot
ms.date: 2026-04-01
ms.topic: how-to
keywords:
  - implementation
  - architecture
  - audio capture
  - analytics
  - windows desktop
estimated_reading_time: 10
---

## Summary

The first implementation pass established a buildable Windows desktop app that can act as a speaking-pace sidecar during Teams calls. The work focused on getting the core product loop in place before adding transcript-based speech intelligence.

The current version includes:

* A native Windows desktop shell with tray behavior and an always-on-top overlay
* Shared-mode microphone capture through WASAPI by way of NAudio
* Live pace estimation from audio signal activity and pause structure
* Visual alert states for calm, caution, and critical pacing
* Local persistence for app settings and recent session summaries
* Root-level launch scripts for visible and tray startup
* In-app troubleshooting UI backed by a file-based diagnostics log
* A self-contained published Windows build and a custom app, window, and tray icon

## Decisions made during implementation

The original research direction favored a native Windows shell with shared microphone capture and on-device processing. During implementation, one practical adjustment was necessary.

* The shell uses WPF on .NET 10 instead of WinUI 3 because the local machine did not have the WinUI or Windows App SDK templates installed.
* The audio layer uses `NAudio` to keep WASAPI capture manageable in C#.
* The first pace engine is signal-derived so the app remains local-first and buildable without introducing an on-device ASR stack in the same pass.
* Settings and recent sessions are stored in a local JSON file to keep the persistence layer simple and inspectable.

## Work completed

### Tooling and scaffold setup

The following setup work was completed first:

* Verified the installed .NET SDKs and project templates
* Confirmed that WPF templates were available and WinUI templates were not
* Created the root solution file `PaceApp.slnx`
* Created the five solution projects under `src`
* Added project references between the app shell and supporting libraries
* Added the `NAudio` package to the audio project
* Added a standard `.gitignore`

### Desktop shell implementation

The desktop shell was built in `src/PaceApp.App`.

Created or updated:

* `App.xaml`
* `App.xaml.cs`
* `MainWindow.xaml`
* `MainWindow.xaml.cs`
* `Services/PaceMonitorController.cs`
* `Services/StartupRegistrationService.cs`
* `ViewModels/ObservableObject.cs`
* `ViewModels/RelayCommand.cs`
* `ViewModels/AsyncRelayCommand.cs`
* `ViewModels/MainWindowViewModel.cs`
* `ViewModels/SessionSummaryItemViewModel.cs`

Implemented behavior:

* Tray icon lifecycle
* Explicit Hide-to-tray behavior through the `Hide` action, while the standard window close exits cleanly
* Optional start-with-Windows toggle through the current-user Run key
* Compact live coaching surface with current WPM, trend, pause metrics, clarity proxy, and recent sessions
* Commands to start monitoring, stop monitoring, hide the window, and exit the app
* Single-instance handoff so a visible relaunch requests the existing app instance to show its window
* Diagnostics panel in the main window with recent startup and runtime messages
* Async startup initialization so the WPF UI thread does not deadlock during app startup

Additional files created during the launcher and troubleshooting pass:

* `Services/AppDiagnosticsService.cs`
* `Assets/PaceCoach.ico`
* `../../Start-PaceApp.ps1`
* `../../Start-PaceApp.cmd`
* `../../Start-PaceApp-Tray.cmd`

### Shared contracts and models

The shared types live in `src/PaceApp.Core`.

Created:

* `Abstractions/IMicrophoneCaptureService.cs`
* `Abstractions/IPaceMetricsEngine.cs`
* `Abstractions/IAppStateRepository.cs`
* `Models/AudioFrame.cs`
* `Models/CaptureStatusUpdate.cs`
* `Models/LivePaceSnapshot.cs`
* `Models/SessionMetricPoint.cs`
* `Models/SessionSummary.cs`
* `Models/AppSettings.cs`
* `Models/PaceAlertLevel.cs`

These contracts are the seam between the shell, capture layer, analytics layer, and persistence layer.

### Audio capture layer

The audio implementation lives in `src/PaceApp.Audio`.

Created:

* `Services/MicrophoneCaptureService.cs`
* `Services/AudioDeviceWatcher.cs`

Implemented behavior:

* Open the default Windows communications microphone in shared mode
* Fall back to the console capture device when needed
* Convert captured audio to mono float samples for downstream processing
* Emit `AudioFrame` objects to the analytics pipeline
* Watch for default-device changes and restart capture against the new device
* Surface status updates for startup, pause, and capture failure conditions

### Analytics layer

The pace engine lives in `src/PaceApp.Analytics`.

Created:

* `Services/SignalPaceMetricsEngine.cs`

Implemented behavior:

* Track rolling audio-energy envelope changes to estimate syllable-like peaks
* Estimate pace over short and long windows from recent peak activity
* Track speech ratio over rolling windows
* Detect pauses and compute pause rate and average pause duration
* Compute a simple trend value from recent WPM history
* Compute a clarity proxy from speed, speech density, and pause quality
* Apply hysteresis-aware calm, caution, and critical alert states
* Require stronger syllable-peak evidence and sustained speech before warning states activate so short or slow phrases are less likely to trigger false red alerts
* Build a session summary at the end of monitoring

### Persistence layer

The persistence implementation lives in `src/PaceApp.Infrastructure`.

Created:

* `Services/JsonAppStateRepository.cs`

Implemented behavior:

* Load and save application settings
* Load and save recent session summaries
* Keep the state file under `%LocalAppData%\PaceApp\state.json`
* Limit saved session history to the most recent fifty sessions
* Write startup and troubleshooting events to `%LocalAppData%\PaceApp\diagnostics.log`

## Current project structure

```text
PaceApp/
  PaceApp.slnx
  README.md
  .gitignore
  Start-PaceApp.cmd
  Start-PaceApp-Tray.cmd
  Start-PaceApp.ps1
  docs/
    IMPLEMENTATION.md
    images/
      pace-coach-ui.png
  src/
    PaceApp.App/
    PaceApp.Audio/
    PaceApp.Analytics/
    PaceApp.Core/
    PaceApp.Infrastructure/
```

## Runtime flow

The current runtime flow is straightforward.

1. The desktop shell starts and loads saved settings and recent session history.
2. When monitoring starts, the shell asks the audio layer to bind to the default communications microphone.
3. The audio layer emits normalized `AudioFrame` objects.
4. The analytics layer converts those frames into live pacing metrics and alert states.
5. The shell updates the overlay view model and redraws the live coaching UI.
6. The diagnostics service records startup and troubleshooting events to a local log and surfaces recent messages in the main window.
7. When monitoring stops, the analytics layer returns a `SessionSummary` and the infrastructure layer persists it locally.

## Packaging and recent stabilization work

After the first scaffold pass, the app received several operational fixes and packaging improvements.

* Reworked startup so the app awaits async initialization instead of blocking the WPF UI thread.
* Tightened the single-instance show-window handoff so a second visible launch reopens the existing window reliably.
* Added a custom Pace Coach icon for the executable, main window, and notification tray.
* Created a self-contained publish flow under `published\PaceCoach-win-x64` for direct double-click launching.
* Tightened the live pace detector after a user report that slow phrases could still trigger red warnings.

## Validation work completed

The following checks were completed during implementation:

* Verified the local .NET environment before scaffolding
* Built the full solution successfully with:

```powershell
dotnet build .\PaceApp.slnx
```

* Launched the WPF app as a smoke test with:

```powershell
dotnet run --project .\src\PaceApp.App\PaceApp.App.csproj
```

* Validated the supported visible launcher with:

```powershell
.\Start-PaceApp.cmd
```

* Published and launched the self-contained Windows build with:

```powershell
dotnet publish .\src\PaceApp.App\PaceApp.App.csproj -c Release -r win-x64 --self-contained true -o .\published\PaceCoach-win-x64
```

* Verified that a second visible launch reopens the existing app window instead of spawning a duplicate hidden instance.

The visible launcher was verified after fixing a startup XAML binding bug that originally caused the PaceApp error dialog during window creation.

The diagnostics log exposed that failure clearly: a `ProgressBar` binding attempted a write-capable binding against the read-only `CurrentWpm` property. That binding was changed to explicit one-way mode, and the visible launcher now starts the app successfully.

## Known limitations in the current version

The current build is a strong first scaffold, but it is not the finished product yet.

* Pace is estimated from signal activity, not transcript-derived word count.
* There is no on-device ASR pipeline yet.
* Teams is treated as a normal microphone consumer. There is no Teams-specific SDK integration.
* Device change handling uses polling rather than a richer device-notification mechanism.
* The app has not yet been calibrated against multiple real call recordings.
* There are no automated tests yet.
* There is a generated self-contained publish folder, but there is not yet a formal installer.
* The raw `dotnet run` path for the WPF project can be less predictable than the provided root launcher because GUI app lifetime and WPF-generated-file recovery are easier to manage through the script.

## Recommended next implementation steps

The next engineering pass should focus on quality rather than more shell work.

* Introduce a local ASR path to replace the signal-derived WPM estimate.
* Add calibration and threshold tuning against real headset and laptop microphone recordings.
* Add richer diagnostics for exclusive-mode failures, permission issues, and mid-call device switches.
* Package the app for easier local installation once the live metrics stabilize.
