# JollyRouteViewer

A Windows desktop GPS activity viewer and route editor built with **C#**, **WPF** and **.NET 10**. Load cycling, running or walking activities in GPX or FIT format, inspect them on an interactive map, edit the route and timing, and export the result.

## Features

### Maps and route editing

- Interactive Leaflet map hosted in Microsoft Edge WebView2.
- Selectable CARTO Light/Dark, MapTiler Streets/Outdoor and OpenStreetMap map layers (subject to provider terms).
- Automatically display and fit a loaded GPS route to the map.
- Draggable route markers, spaced approximately every **1 km** by default; adjust the spacing or display all GPS points.
- Moving a marker smoothly displaces neighbouring GPS samples between adjacent displayed markers. **No automatic road snapping.**
- Reverse a route and undo/redo edits.

### Activity information

The right-hand panel shows available activity details, including:

- Activity name and type
- Distance, elapsed time and estimated moving time
- Average speed, average pace and maximum recorded speed
- Elevation gain and loss
- Average heart rate, cadence and power, when present
- Start time and marker count

### Elevation, heart rate and power graph

- Distance-based elevation profile.
- Optional **heart-rate (BPM)** and **power (watts)** traces, each with an independent scale and show/hide checkbox.
- Hover over the graph to locate the corresponding point on the map.
- Drag across the graph to select a section. The corresponding route segment is highlighted on the map.
- Missing sensor readings are not fabricated.

### Section speed editor

Select a section of the graph and either:

1. **Save seconds** — reduce the selected section's duration by a specified amount.
2. **Target average speed** — retime the selected section to a chosen km/h average.

The editor adjusts timestamps within the selected section, shifts later timestamps to maintain continuity and recalculates applicable speed values. Time editing requires valid chronological timestamps for the GPS points. These edits change recorded activity data, **not actual physical performance**.

### Import, export and FIT metadata

| Import | Export | Notes |
|---|---|---|
| GPX | GPX | Retains supported track data and extensions |
| GPX | FIT | Creates a FIT activity; requires timestamps |
| FIT | GPX | Converts supported route and sensor fields |
| FIT | FIT | Attempts to preserve the original FIT structure and unrelated metadata |

FIT export attempts to retain original message order, device information, developer fields and unknown fields while updating supported route records and activity summaries. If preservation cannot safely handle a file, the application may offer a regenerated FIT export with reduced metadata fidelity.

**Important:** FIT preservation is not fully validated for every device and profile. Multi-lap activities, compressed timestamps, event/pause messages, manufacturer-specific metrics and some derived summaries may require additional handling. GPX/FIT conversions can lose fields unsupported by the destination format. Keep your original files and validate exported FIT activities before uploading them to other services.

## Requirements

- Windows 10/11
- Visual Studio with the .NET desktop development workload
- .NET 10 SDK
- Microsoft Edge WebView2 Runtime
- Internet access for online map tiles

The project references `Microsoft.Web.WebView2` via NuGet. Leaflet and map resources are loaded by the application's map page.

## Build and run

1. Open `JollyRouteViewer.csproj` in Visual Studio.
2. Restore NuGet packages.
3. Build and run the WPF application.
4. Open a `.gpx` or `.fit` file.
5. Choose a map provider, explore the route, and use the graph or markers to make edits.
6. Export to GPX or FIT using the Save/Export control.

## Map providers and API keys

CARTO Light is the default. MapTiler Streets and Outdoor require a MapTiler API key and are subject to MapTiler's usage terms. The original settings UI supports entering a key and saving map preferences in:

`%LOCALAPPDATA%\JollyRouteViewer\map-settings.json`

If you have customised the application to embed a MapTiler key in `MapConfiguration.cs`, ensure all startup and provider-switching paths use that value instead of the saved or textbox key. **Do not commit a real API key to a public repository.** Client-side keys are extractable; apply appropriate restrictions and usage limits. OpenStreetMap public tile access is subject to its tile usage policy.

## Project layout

```text
JollyRouteViewer/
├── App.xaml
├── MainWindow.xaml
├── MainWindow.xaml.cs
├── Configuration/
│   └── MapConfiguration.cs
├── Map/
│   └── map.html
├── Models/
│   └── RoutePoint.cs
├── Services/
│   ├── FitService.cs
│   ├── FitWriter.cs
│   ├── FitPreservingWriter.cs
│   ├── GpxService.cs
│   ├── MapSettingsService.cs
│   ├── RouteMath.cs
│   ├── RouteEditService.cs
│   ├── RouteReverseService.cs
│   └── TimeEditService.cs
└── ViewModels/
    └── MainViewModel.cs
```

## Current limitations

- Route editing is geometric; it does not snap points to roads or fetch replacement elevations.
- Graph data depends on values actually present in the imported activity.
- Moving-time estimates may differ from the recording device's calculations.
- FIT preservation and export should be tested against real device files and a FIT validator.
- Map tiles require network access and compliance with provider policies.

## Development status

The source and recent graph updates have been inspected, but a complete Windows build and end-to-end FIT export validation have not been performed here. Treat this as an actively developed project and keep backups of original activities.
