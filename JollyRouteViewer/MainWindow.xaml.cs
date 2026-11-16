using JollyRouteViewer.Configuration;
using JollyRouteViewer.Models;
using JollyRouteViewer.Services;
using JollyRouteViewer.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
namespace JollyRouteViewer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private bool _ready;
    private readonly MapSettingsService _mapSettings = new();
    private bool _tileWarningShown;
    private readonly Stack<RouteSnapshot> _undo = new();
    private readonly Stack<RouteSnapshot> _redo = new();
    private List<int> _markers = [];
    private readonly HashSet<int> _selectedMarkers = [];
    private bool _fitOnNextDraw = true;
    private int _sectionStart = -1, _sectionEnd = -1;
    private sealed record RouteSnapshot(RoutePoint[] Points);
    private static RoutePoint Clone(RoutePoint p) => new() { Latitude = p.Latitude, Longitude = p.Longitude, Elevation = p.Elevation, Time = p.Time, Distance = p.Distance, Speed = p.Speed, HeartRate = p.HeartRate, Cadence = p.Cadence, Power = p.Power };

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        MapProviderCombo.SelectedValue = _mapSettings.Settings.Provider;
        if (MapProviderCombo.SelectedIndex < 0)
            MapProviderCombo.SelectedIndex = 0;

        MapKeyBox.Password = _mapSettings.Settings.MapTilerKey;
        Loaded += InitializeMap;
        StateChanged += MainWindow_StateChanged;
    }

    private async void InitializeMap(object sender, RoutedEventArgs e)
    {
        try
        {
            await MapView.EnsureCoreWebView2Async();
            MapView.CoreWebView2.WebMessageReceived += OnMapMessage;
            MapView.CoreWebView2.NavigationCompleted += async (_, args) =>
            {
                _ready = args.IsSuccess;
                if (_ready)
                {
                    await ApplyMapProviderAsync();
                    DrawRoute();
                }
                else
                    StatusText.Text = "Map failed to load";
            };

            MapView.CoreWebView2.SetVirtualHostNameToFolderMapping("jollyrouteviewer.local", Path.Combine(AppContext.BaseDirectory, "Map"), CoreWebView2HostResourceAccessKind.DenyCors);
            MapView.CoreWebView2.Navigate("https://jollyrouteviewer.local/map.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WebView2 initialization failed: {ex.Message}\nInstall the Microsoft Edge WebView2 Runtime.");
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (this.WindowState == WindowState.Maximized)
        {
            RestoreButton.Visibility = Visibility.Visible;
            MaximizeButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            MaximizeButton.Visibility = Visibility.Visible;
            RestoreButton.Visibility = Visibility.Collapsed;
        }
    }

    private void CommandBinding_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = true;
    }

    private void CommandBinding_Executed_Minimize(object sender, ExecutedRoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void CommandBinding_Executed_Maximize(object sender, ExecutedRoutedEventArgs e)
    {
        SystemCommands.MaximizeWindow(this);
    }

    private void CommandBinding_Executed_Restore(object sender, ExecutedRoutedEventArgs e)
    {
        SystemCommands.RestoreWindow(this);
    }

    private void CommandBinding_Executed_Close(object sender, ExecutedRoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }

    private async void MapSettingsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
            return;

        await ApplyMapProviderAsync();
    }

    private async void ApplyMapKey_Click(object sender, RoutedEventArgs e)
    {
        await ApplyMapProviderAsync();
    }

    private async Task ApplyMapProviderAsync()
    {
        if (!_ready || MapProviderCombo.SelectedItem is not ComboBoxItem item || MapView.CoreWebView2 is null)
            return;

        string provider = item.Tag?.ToString() ?? "cartoLight";

        // Always use the embedded MapTiler API key.
        string key = "ZV9GYYJsaplaK0iv2Wwt"; // provider.StartsWith("maptiler", StringComparison.OrdinalIgnoreCase) ? MapConfiguration.MapTilerApiKey.Trim() : string.Empty;

        if (provider.StartsWith("maptiler", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(key))
        {
            StatusText.Text = "MapTiler API key is not configured.";
            return;
        }

        try
        {
            // Send the selected provider and embedded key to Leaflet.
            string script = $"window.setMapProvider({JsonSerializer.Serialize(provider)}, {JsonSerializer.Serialize(key)})";

            await MapView.CoreWebView2.ExecuteScriptAsync(script);

            // Save provider preference only.
            _mapSettings.Settings.Provider = provider;
            _mapSettings.Settings.MapTilerKey = string.Empty;
            _mapSettings.Save();

            _tileWarningShown = false;
            StatusText.Text = $"Map: {item.Content}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Map provider error: {ex.Message}";
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "GPS activities (*.gpx;*.fit)|*.gpx;*.fit|GPX files (*.gpx)|*.gpx|FIT files (*.fit)|*.fit" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var activity = Path.GetExtension(dlg.FileName).Equals(".fit", StringComparison.OrdinalIgnoreCase) ? FitService.Load(dlg.FileName) : GpxService.Load(dlg.FileName);
            if (activity.Points.Count == 0)
                throw new InvalidDataException("No activity records found in file");

            _undo.Clear();
            _redo.Clear();
            _selectedMarkers.Clear();
            UpdateHistoryButtons();
            _sectionStart = -1;
            _sectionEnd = -1;
            _fitOnNextDraw = true;
            _vm.Activity = activity;
            if (ActivityTypeCombo is not null)
            {
                var match = ActivityTypeCombo.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), activity.Type, StringComparison.OrdinalIgnoreCase));
                if (match is not null) ActivityTypeCombo.SelectedItem = match;
            }

            PoolLengthBox?.Text = activity.PoolLengthMetres?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "25";
            RefreshActivityStartEditor();
            StatusText.Text = $"Loaded {activity.Points.Count:N0} records" + (activity.Points.All(p => !p.Latitude.HasValue) ? " (indoor activity; no GPS)" : "");
            DrawRoute();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to open activity", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void ApplyPoolLength_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Activity is not { } activity || !activity.IsSwimming)
            return;

        if (!double.TryParse(PoolLengthBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out double length) || length <= 0 || length > 200)
        {
            MessageBox.Show("Enter a valid pool length in metres (up to 200).", "Pool length");
            return;
        }

        activity.PoolLengthMetres = length;
        if (activity.SwimLengths.Count > 0)
        {
            double metres = 0;
            int offset = activity.Points.Count == activity.SwimLengths.Count + 1 ? 1 : 0;
            for (int i = 0; i < activity.SwimLengths.Count && i + offset < activity.Points.Count; i++)
            {
                if (!activity.SwimLengths[i].IsRest) 
                    metres += length;

                activity.Points[i + offset].Distance = metres;
            }
        }
        _vm.Refresh();
        StatusText.Text = $"Pool length set to {length:0.##} m.";
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Activity is null)
            return;

        var dlg = new SaveFileDialog { Filter = "GPX files (*.gpx)|*.gpx|FIT activity files (*.fit)|*.fit", FileName = _vm.Activity.Name + "-edited.gpx", AddExtension = true };
        if (dlg.ShowDialog() != true) 
            return;

        try
        {
            if (dlg.FilterIndex == 2)
            {
                if (_vm.Activity.OriginalFitBytes is not null && !_vm.Activity.IsSwimming && _vm.Activity.Points.All(p => p.Latitude.HasValue && p.Longitude.HasValue))
                {
                    try { FitPreservingWriter.Save(_vm.Activity, dlg.FileName); }
                    catch (Exception preservationError)
                    {
                        var answer = MessageBox.Show(
                            $"The original FIT layout cannot safely preserve these edits:\n{preservationError.Message}\n\n" +
                            "Export a newly generated FIT instead? This will discard unknown/device/developer metadata.",
                            "FIT preservation unavailable", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        if (answer != MessageBoxResult.Yes) 
                            return;

                        FitWriter.Save(_vm.Activity, dlg.FileName);
                    }
                }
                else 
                    FitWriter.Save(_vm.Activity, dlg.FileName);
            }
            else if (_vm.Activity.Points.All(p => p.Latitude.HasValue && p.Longitude.HasValue)) 
                GpxService.Save(_vm.Activity, dlg.FileName);
            else 
                throw new InvalidOperationException("GPX requires GPS positions. Export this indoor activity as FIT instead.");

            StatusText.Text = $"Exported {Path.GetExtension(dlg.FileName).ToUpperInvariant()} with updated activity data";
        }
        catch (Exception ex)
        { 
            MessageBox.Show(ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error); 
        }
    }

    private void Reverse_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Activity is null || _vm.Activity.Points.Count < 2) 
            return;

        _undo.Push(Snapshot()); _redo.Clear();
        RouteReverseService.Reverse(_vm.Activity);
        _sectionStart = -1;
        _sectionEnd = -1;
        _selectedMarkers.Clear(); 
        _fitOnNextDraw = true;
        _vm.Refresh();
        UpdateHistoryButtons(); 
        UpdateSection(); 
        DrawRoute();
        StatusText.Text = "Route reversed; timestamps and speeds recalculated. Export GPX or FIT to save.";
    }

    private async void DrawRoute()
    {
        if (!_ready || _vm.Activity is null) 
            return;

        var p = _vm.Activity.Points;
        _markers = RouteMath.MarkerIndices(p, _vm.SpacingKm * 1000, _vm.AllPoints);
        var visibleMarkers = _vm.AllPoints ? [.. _markers] : _sectionStart >= 0 && _sectionEnd > _sectionStart ? [.. _markers.Where(i => i >= _sectionStart && i <= _sectionEnd)] : Array.Empty<int>();
        var data = new 
        { 
            selectedMarkers = _selectedMarkers.ToArray(), 
            points = p.Select(x => x.Latitude.HasValue && x.Longitude.HasValue ? new double?[] { x.Latitude, x.Longitude } : null).ToArray(), 
            markers = visibleMarkers, 
            fit = _fitOnNextDraw, 
            elevations = p.Select(x => x.Elevation).ToArray(), 
            heartRates = p.Select(x => x.HeartRate).ToArray(), 
            powers = p.Select(x => x.Power).ToArray(), 
            cadences = p.Select(x => x.Cadence).ToArray(), 
            speeds = p.Select(x => x.Speed.HasValue ? (double?)(x.Speed.Value * 3.6) : null).ToArray(), 
            times = p.Select(x => x.Time?.ToString("O")).ToArray(), 
            cumulative = RouteMath.Cumulative(p), 
            sectionStart = _sectionStart, 
            sectionEnd = _sectionEnd };
        _fitOnNextDraw = false;
        string json = JsonSerializer.Serialize(data);
        try 
        {
            await MapView.CoreWebView2.ExecuteScriptAsync($"window.renderRoute({json})");
        }
        catch (Exception ex)
        { 
            StatusText.Text = $"Map update failed: {ex.Message}";
        }
    }

    private async void HeartRateVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || MapView?.CoreWebView2 is null) return;
        bool visible = ShowHeartRateCheck?.IsChecked == true;
        try
        {
            await MapView.CoreWebView2.ExecuteScriptAsync($"window.setHeartRateVisible({(visible ? "true" : "false")})");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Heart rate graph error: {ex.Message}";
        }
    }
    private async void PowerVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || MapView?.CoreWebView2 is null) return;
        bool visible = ShowPowerCheck?.IsChecked == true;
        try
        {
            await MapView.CoreWebView2.ExecuteScriptAsync($"window.setPowerVisible({(visible ? "true" : "false")})");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Power graph error: {ex.Message}";
        }
    }
    private async void CadenceVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || MapView?.CoreWebView2 is null) 
            return;

        try 
        { 
            await MapView.CoreWebView2.ExecuteScriptAsync($"window.setCadenceVisible({(ShowCadenceCheck?.IsChecked == true ? "true" : "false")})"); 
        }
        catch (Exception ex) 
        { 
            StatusText.Text = $"Cadence graph error: {ex.Message}"; 
        }
    }
    private async void SpeedVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || MapView?.CoreWebView2 is null)
            return;

        try
        {
            await MapView.CoreWebView2.ExecuteScriptAsync($"window.setSpeedVisible({(ShowSpeedCheck?.IsChecked == true ? "true" : "false")})"); 
        }
        catch (Exception ex)
        { 
            StatusText.Text = $"Speed graph error: {ex.Message}";
        }
    }
    private void ActivityTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm?.Activity is null || ActivityTypeCombo?.SelectedItem is not ComboBoxItem item) 
            return;

        _vm.Activity.Type = item.Tag?.ToString() ?? "Other";
        _vm.Refresh();
    }
    private void MarkersChanged(object sender, RoutedEventArgs e)
    {
        if (_vm is null || SpacingCombo?.SelectedItem is not ComboBoxItem item) 
            return;

        if (double.TryParse(item.Content.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var spacing)) 
            _vm.SpacingKm = spacing;

        _vm.AllPoints = AllPointsCheck?.IsChecked == true;
        DrawRoute();
    }
    private async void Fit_Click(object sender, RoutedEventArgs e) 
    { 
        if (_ready) 
            await MapView.CoreWebView2.ExecuteScriptAsync("window.fitRoute()");
    }

    private RouteSnapshot Snapshot() => new([.. _vm.Activity!.Points.Select(Clone)]);
    private void Restore(RouteSnapshot snapshot)
    {
        if (_vm.Activity is null) return;
        _vm.Activity.Points.Clear();
        _vm.Activity.Points.AddRange(snapshot.Points.Select(Clone));
        _sectionStart = -1; _sectionEnd = -1; _selectedMarkers.Clear();
        _vm.Refresh(); RefreshActivityStartEditor(); DrawRoute(); UpdateHistoryButtons(); UpdateSection();
    }

    private void UpdateSection()
    {
        if (_vm.Activity is null || _sectionStart < 0 || _sectionEnd <= _sectionStart) 
        {
            SectionInfo.Text = "Drag across the elevation graph to select a section."; 
            return; 
        }

        var p = _vm.Activity.Points;
        var distances = RouteMath.Cumulative(p);
        double km = (distances[_sectionEnd] - distances[_sectionStart]) / 1000;
        string duration = p[_sectionStart].Time is DateTimeOffset a && p[_sectionEnd].Time is DateTimeOffset b ? " · " + (b - a).ToString(@"hh\:mm\:ss") : " · no timestamps";
        SectionInfo.Text = $"{distances[_sectionStart] / 1000:F2}–{distances[_sectionEnd] / 1000:F2} km ({km:F2} km){duration}";
    }
    private void RefreshActivityStartEditor()
    {
        var first = _vm.Activity?.Points.FirstOrDefault()?.Time;
        bool available = first.HasValue;
        if (ActivityStartDate is null || ActivityStartTime is null || ApplyActivityStartButton is null)
            return;

        ActivityStartDate.IsEnabled = available;
        ActivityStartTime.IsEnabled = available;
        ApplyActivityStartButton.IsEnabled = available;
        if (available)
        {
            var local = first!.Value.ToLocalTime();
            ActivityStartDate.SelectedDate = local.Date;
            ActivityStartTime.Text = local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }
        else
        {
            ActivityStartDate.SelectedDate = null;
            ActivityStartTime.Text = string.Empty;
        }
    }

    private void ApplyActivityStart_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Activity is null || _vm.Activity.Points.Count == 0 || _vm.Activity.Points[0].Time is not DateTimeOffset originalStart)
            return;

        if (ActivityStartDate.SelectedDate is not DateTime date || !TimeSpan.TryParseExact(ActivityStartTime.Text.Trim(), [@"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss"], CultureInfo.InvariantCulture, out TimeSpan clock) || clock >= TimeSpan.FromDays(1))
        {
            MessageBox.Show("Choose a date and enter a start time as HH:mm or HH:mm:ss.", "Invalid activity start", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            // Interpret the entered date/time in the computer's local time zone.
            // This also handles daylight-saving transitions when changing dates.
            DateTime localDateTime = DateTime.SpecifyKind(date.Date.Add(clock), DateTimeKind.Unspecified);
            if (TimeZoneInfo.Local.IsInvalidTime(localDateTime))
                throw new ArgumentException("This local time does not exist because of a daylight-saving clock change.");

            var newStart = new DateTimeOffset(localDateTime, TimeZoneInfo.Local.GetUtcOffset(localDateTime));
            TimeSpan shift = newStart - originalStart;
            if (shift == TimeSpan.Zero) 
                return;

            // Check for overflow before changing any point.
            foreach (var point in _vm.Activity.Points)
                if (point.Time.HasValue) _ = point.Time.Value.Add(shift);

            _undo.Push(Snapshot());
            _redo.Clear();
            foreach (var point in _vm.Activity.Points)
                if (point.Time.HasValue) point.Time = point.Time.Value.Add(shift);

            _vm.Refresh();
            PoolLengthBox?.Text = _vm.Activity.PoolLengthMetres?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "25"; RefreshActivityStartEditor();

            UpdateHistoryButtons();
            UpdateSection();
            StatusText.Text = $"Activity start changed to {newStart:dd MMM yyyy HH:mm:ss}. Export GPX or FIT to save.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to change activity start", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ApplyTime_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Activity is null || _sectionStart < 0 || _sectionEnd <= _sectionStart)
        { 
            MessageBox.Show("Select a section by dragging across the elevation profile first.");
            return;
        }

        if (!double.TryParse(TimeValue.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out double value))
        { 
            MessageBox.Show("Enter a valid number."); 
            return;
        }
        try
        {
            var snapshot = Snapshot();
            bool seconds = TimeMode.SelectedIndex == 0;
            var (distanceKm, oldSeconds, newSeconds) = TimeEditService.Apply(_vm.Activity.Points, _sectionStart, _sectionEnd, seconds ? value : null, seconds ? null : value);
            _undo.Push(snapshot); _redo.Clear(); UpdateHistoryButtons(); _vm.Refresh(); UpdateSection(); DrawRoute();
            StatusText.Text = $"Section {distanceKm:F2} km: {oldSeconds:F0}s → {newSeconds:F0}s. Export GPX or FIT to save.";
        }
        catch (Exception ex) 
        { 
            MessageBox.Show(ex.Message, "Unable to change section timing", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateDeleteButton()
    {
        if (DeleteMarkersButton is not null)
        {
            DeleteMarkersButton.IsEnabled = _vm.Activity is not null && _selectedMarkers.Any(i => i > 0 && i < _vm.Activity.Points.Count - 1);
            DeleteMarkersButton.Content = $"Delete selected markers ({_selectedMarkers.Count})";
        }
    }

    private void DeleteMarkers_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Activity is null) 
            return;

        var points = _vm.Activity.Points;
        // Keep start and finish, preventing accidental destruction of the route.
        var toDelete = _selectedMarkers.Where(i => i > 0 && i < points.Count - 1).Distinct().OrderByDescending(i => i).ToArray();
        if (toDelete.Length == 0)
            return;

        _undo.Push(Snapshot()); _redo.Clear();
        // Compress the timeline into the original sample slots. All sensor data,
        // position and elevation remain attached to their surviving records.
        var originalTimes = points.Select(p => p.Time).ToArray();
        foreach (int i in toDelete) points.RemoveAt(i);
        if (originalTimes.All(t => t.HasValue))
            for (int i = 0; i < points.Count; i++) 
                points[i].Time = originalTimes[i];

        _selectedMarkers.Clear(); _sectionStart = -1; _sectionEnd = -1;
        _vm.Refresh(); UpdateHistoryButtons(); UpdateDeleteButton(); UpdateSection(); DrawRoute();
        StatusText.Text = $"Deleted {toDelete.Length} GPS point(s). Undo available; export to save.";
    }

    private void UpdateHistoryButtons() 
    {
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
        UpdateDeleteButton(); 
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Activity is null || _undo.Count == 0) return;
        _redo.Push(Snapshot()); Restore(_undo.Pop()); StatusText.Text = "Undid route edit";
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Activity is null || _redo.Count == 0) 
            return;

        _undo.Push(Snapshot()); Restore(_redo.Pop()); StatusText.Text = "Redid route edit";
    }

    private void OnMapMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.GetProperty("type").GetString() == "tileError")
            {
                if (!_tileWarningShown)
                {
                    _tileWarningShown = true;
                    StatusText.Text = "Map tiles blocked or unavailable. Try another provider or check your API key.";
                }
                return;
            }

            if (_vm.Activity is null) 
                return;

            if (root.GetProperty("type").GetString() == "selectionClear")
            {
                _sectionStart = -1;
                _sectionEnd = -1;
                UpdateSection();
                DrawRoute();
                return;
            }

            if (root.GetProperty("type").GetString() == "selection")
            {
                int a = root.GetProperty("start").GetInt32(), b = root.GetProperty("end").GetInt32();
                if (a >= 0 && b > a && b < _vm.Activity.Points.Count) { _sectionStart = a; _sectionEnd = b; _selectedMarkers.Clear(); UpdateSection(); DrawRoute(); }
                return;
            }

            if (root.GetProperty("type").GetString() == "markerSelectionSet")
            {
                bool additive = root.TryGetProperty("additive", out var add) && add.GetBoolean();
                if (!additive) _selectedMarkers.Clear();
                foreach (var entry in root.GetProperty("indices").EnumerateArray())
                {
                    int i = entry.GetInt32();
                    if (i > 0 && i < _vm.Activity.Points.Count - 1)
                        _selectedMarkers.Add(i);
                }
                UpdateDeleteButton();
                DrawRoute();
                StatusText.Text = $"{_selectedMarkers.Count} GPS points selected. Delete Selected removes only those points.";
                return;
            }

            if (root.GetProperty("type").GetString() == "markerSelect")
            {
                int selectedIndex = root.GetProperty("index").GetInt32();
                if (selectedIndex <= 0 || selectedIndex >= _vm.Activity.Points.Count - 1) return;
                bool multi = root.TryGetProperty("multi", out var multiProperty) && multiProperty.GetBoolean();
                if (!multi)
                {
                    // Clicking an already selected marker toggles it off.
                    bool wasOnlySelected = _selectedMarkers.Count == 1 && _selectedMarkers.Contains(selectedIndex);
                    _selectedMarkers.Clear();
                    if (!wasOnlySelected) _selectedMarkers.Add(selectedIndex);
                }
                else if (!_selectedMarkers.Add(selectedIndex))
                {
                    _selectedMarkers.Remove(selectedIndex);
                }
                UpdateDeleteButton();
                DrawRoute();
                return;
            }

            if (root.GetProperty("type").GetString() != "move")
                return;

            int index = root.GetProperty("index").GetInt32();
            if (index < 0 || index >= _vm.Activity.Points.Count)
                return;

            double lat = root.GetProperty("lat").GetDouble(), lng = root.GetProperty("lng").GetDouble();
            if (!double.IsFinite(lat) || !double.IsFinite(lng) || Math.Abs(lat) > 90 || Math.Abs(lng) > 180)
                return;

            if (!_markers.Contains(index)) 
                return;

            _undo.Push(Snapshot()); _redo.Clear();
            RouteEditService.MoveMarker(_vm.Activity.Points, _markers, index, lat, lng);
            _vm.Refresh(); UpdateHistoryButtons(); DrawRoute();
            StatusText.Text = $"Reshaped route at point {index + 1}; export GPX or FIT to save";
        }
        catch (JsonException) 
        { 
        }
    }
}
