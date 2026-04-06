---
title: PaceApp Copilot Instructions
description: Workspace guidance for making source changes in PaceApp, locating the published output, and republishing the Windows app after edits
author: GitHub Copilot
ms.date: 2026-04-06
ms.topic: reference
keywords:
  - copilot
  - paceapp
  - publish
  - wpf
  - windows desktop
estimated_reading_time: 5
---

## Project overview

PaceApp is a Windows desktop sidecar that monitors speaking pace during Teams calls. The current shell is WPF on .NET 10. Audio capture uses NAudio and WASAPI shared mode. Live pacing uses Vosk offline speech recognition for transcript-backed WPM with word-level timing, falling back to signal-derived estimation when Vosk is unavailable.

## Source layout

Use the source projects under `src` as the only place for code changes.

* `src/PaceApp.App`: WPF shell, tray behavior, startup flow, view models, diagnostics UI, icon asset
* `src/PaceApp.Core`: shared models and interfaces
* `src/PaceApp.Audio`: microphone capture and device watching
* `src/PaceApp.Analytics`: live pace estimation and alert logic
* `src/PaceApp.Infrastructure`: local JSON persistence

Do not hand-edit files inside `published`. That folder is generated output.

## Published output

The current local published app folder is:

```text
published\PaceCoach-win-x64
```

The double-clickable executable is:

```text
published\PaceCoach-win-x64\PaceApp.App.exe
```

The screenshot used by the README is stored at:

```text
docs\images\pace-coach-ui.png
```

## Preferred edit workflow

When making source changes:

1. Edit files under `src`.
2. Build the desktop project.
3. Run the source launcher to verify behavior.
4. Republish the app if the change affects the end-user build.
5. Update `README.md` and `docs/IMPLEMENTATION.md` when user-facing behavior, publish steps, or runtime expectations change.

## Build and run commands

Build the desktop app project:

```powershell
dotnet build .\src\PaceApp.App\PaceApp.App.csproj
```

Run the visible source workflow:

```powershell
.\Start-PaceApp.cmd
```

Run the tray-only source workflow:

```powershell
.\Start-PaceApp-Tray.cmd
```

## Republish after source changes

After changing the source code, regenerate the published build with:

```powershell
dotnet publish .\src\PaceApp.App\PaceApp.App.csproj -c Release -r win-x64 --self-contained true -o .\published\PaceCoach-win-x64
```

To build the installer after publishing:

```powershell
.\Build-Installer.ps1
```

The installer output is `installer\Output\PaceCoach-Setup.exe`.

If you want a zip archive after publishing, create it with:

```powershell
Compress-Archive -Path .\published\PaceCoach-win-x64\* -DestinationPath .\published\PaceCoach-win-x64.zip
```

Make sure PaceApp is not running before you overwrite or zip the published output.

## Project-specific notes

Keep these details in mind when changing the app:

* Startup is async in `src/PaceApp.App/App.xaml.cs`; do not change it back to blocking waits on the UI thread.
* The app is intentionally single-instance. A second visible launch should signal the existing instance to show its window.
* The `Hide` button sends the app to the tray, but the standard window close exits the process.
* The app, window, and tray icon all come from `src/PaceApp.App/Assets/PaceCoach.ico`. Keep those references in sync if the icon changes.
* The pacing engine in `src/PaceApp.Analytics/Services/SignalPaceMetricsEngine.cs` is approximate and sensitive to threshold changes. If you tune it, verify both fast detection and false-positive behavior.
* Theme ResourceDictionaries live in `src/PaceApp.App/Themes/`. MainWindow.xaml uses `DynamicResource` for structural colors. Add new color keys to all three theme files when extending the UI.
* Session grade thresholds are in `SignalPaceMetricsEngine.ComputeSessionGrade`. The feedback popup is `SessionFeedbackWindow.xaml`.