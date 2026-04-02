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
* Hide-to-tray behavior instead of closing the window
* Optional start-with-Windows toggle through the current-user Run key
* Compact live coaching surface with current WPM, trend, pause metrics, clarity proxy, and recent sessions
* Commands to start monitoring, stop monitoring, hide the window, and exit the app

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

## Current project structure

```text
PaceApp/
  PaceApp.slnx
  README.md
  .gitignore
  docs/
    IMPLEMENTATION.md
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
6. When monitoring stops, the analytics layer returns a `SessionSummary` and the infrastructure layer persists it locally.

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

The smoke test confirmed that the app starts and stays running without an immediate startup exception in the terminal.

## Known limitations in the current version

The current build is a strong first scaffold, but it is not the finished product yet.

* Pace is estimated from signal activity, not transcript-derived word count.
* There is no on-device ASR pipeline yet.
* Teams is treated as a normal microphone consumer. There is no Teams-specific SDK integration.
* Device change handling uses polling rather than a richer device-notification mechanism.
* The app has not yet been calibrated against multiple real call recordings.
* There are no automated tests or packaging assets yet.

## Recommended next implementation steps

The next engineering pass should focus on quality rather than more shell work.

* Introduce a local ASR path to replace the signal-derived WPM estimate.
* Add calibration and threshold tuning against real headset and laptop microphone recordings.
* Add richer diagnostics for exclusive-mode failures, permission issues, and mid-call device switches.
* Package the app for easier local installation once the live metrics stabilize.
