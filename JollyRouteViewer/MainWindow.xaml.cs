using JollyRouteViewer.Configuration;
using JollyRouteViewer.Models;
using JollyRouteViewer.Services;
using JollyRouteViewer.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
    private bool _fitOnNextDraw = true;
    private int _sectionStart = -1, _sectionEnd = -1;
    private sealed record RouteSnapshot(RoutePoint[] Points);

    private static RoutePoint Clone(RoutePoint p) => new()
    {
        Latitude = p.Latitude,
        Longitude = p.Longitude,
        Elevation = p.Elevation,
        Time = p.Time,
        Speed = p.Speed,
        HeartRate = p.HeartRate,
        Cadence = p.Cadence,
        Power = p.Power
    };

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        MapProviderCombo.SelectedValue = _mapSettings.Settings.Provider;
        if (MapProviderCombo.SelectedIndex < 0)
            MapProviderCombo.SelectedIndex = 0;

        MapKeyBox.Password = _mapSettings.Settings.MapTilerKey;
        Loaded += InitializeMap;
    }

    private async void InitializeMap(object sender, RoutedEventArgs e)
    {
        try
        {
            await MapView.EnsureCoreWebView2Async();
            MapView.CoreWebView2.WebMessageReceived += OnMapMessage;
            MapView.CoreWebView2.NavigationCompleted += async (_, args) => { _ready = args.IsSuccess; if (_ready) { await ApplyMapProviderAsync(); DrawRoute(); } else StatusText.Text = "Map failed to load"; };
            MapView.CoreWebView2.SetVirtualHostNameToFolderMapping("jollyrouteviewer.local", Path.Combine(AppContext.BaseDirectory, "Map"), CoreWebView2HostResourceAccessKind.DenyCors);
            MapView.CoreWebView2.Navigate("https://jollyrouteviewer.local/map.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WebView2 initialization failed: {ex.Message}\nInstall the Microsoft Edge WebView2 Runtime.");
        }
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
        string key = "ZV9GYYJsaplaK0iv2Wwt";
        if (provider.StartsWith("maptiler", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(key))
        {
            StatusText.Text = "MapTiler API key is not configured.";
            return;
        }

        try
        {
            string script = $"window.setMapProvider({JsonSerializer.Serialize(provider)}, {JsonSerializer.Serialize(key)})";
            await MapView.CoreWebView2.ExecuteScriptAsync(script);
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
        var dlg = new OpenFileDialog
        {
            Filter = "GPS activities (*.gpx;*.fit)|*.gpx;*.fit|GPX files (*.gpx)|*.gpx|FIT files (*.fit)|*.fit"
        };

        if (dlg.ShowDialog() != true)
            return;

        try
        {
            var activity = Path.GetExtension(dlg.FileName).Equals(".fit", StringComparison.OrdinalIgnoreCase) ? FitService.Load(dlg.FileName) : GpxService.Load(dlg.FileName);
            if (activity.Points.Count == 0)
                throw new InvalidDataException("No GPS points found in file");

            _undo.Clear();
            _redo.Clear();
            UpdateHistoryButtons();
            _sectionStart = -1;
            _sectionEnd = -1;
            _fitOnNextDraw = true;
            _vm.Activity = activity;
            StatusText.Text = $"Loaded {activity.Points.Count:N0} points";
            DrawRoute();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to open activity", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Activity is null)
            return;

        var dlg = new SaveFileDialog
        {
            Filter = "GPX files (*.gpx)|*.gpx|FIT activity files (*.fit)|*.fit",
            FileName = _vm.Activity.Name + "-edited.gpx",
            AddExtension = true
        };

        if (dlg.ShowDialog() != true)
            return;

        try
        {
            if (dlg.FilterIndex == 2)
            {
                if (_vm.Activity.OriginalFitBytes is not null)
                {
                    try
                    {
                        FitPreservingWriter.Save(_vm.Activity, dlg.FileName);
                    }
                    catch (Exception preservationError)
                    {
                        var answer = MessageBox.Show($"The original FIT layout cannot safely preserve these edits:\n{preservationError.Message}\n\nExport a newly generated FIT instead? This will discard unknown/device/developer metadata.", "FIT preservation unavailable", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        if (answer != MessageBoxResult.Yes)
                            return;

                        FitWriter.Save(_vm.Activity, dlg.FileName);
                    }
                }
                else
                    FitWriter.Save(_vm.Activity, dlg.FileName);
            }
            else
                GpxService.Save(_vm.Activity, dlg.FileName);

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

        _undo.Push(Snapshot());
        _redo.Clear();
        RouteReverseService.Reverse(_vm.Activity);
        _sectionStart = -1;
        _sectionEnd = -1;
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
        var data = new
        {
            points = p.Select(x => new[]
        {
            x.Latitude,
            x.Longitude
        }).ToArray(),
            markers = _markers,
            fit = _fitOnNextDraw,
            elevations = p.Select(x => x.Elevation).ToArray(),
            heartRates = p.Select(x => x.HeartRate).ToArray(),
            powers = p.Select(x => x.Power).ToArray(),
            cumulative = RouteMath.Cumulative(p),
            sectionStart = _sectionStart,
            sectionEnd = _sectionEnd
        };
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
        if (!_ready || MapView?.CoreWebView2 is null)
            return;

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
        if (!_ready || MapView?.CoreWebView2 is null)
            return;

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

    private RouteSnapshot Snapshot() => new(_vm.Activity!.Points.Select(Clone).ToArray());

    private void Restore(RouteSnapshot snapshot)
    {
        if (_vm.Activity is null || snapshot.Points.Length != _vm.Activity.Points.Count)
            return;

        _vm.Activity.Points.Clear();
        _vm.Activity.Points.AddRange(snapshot.Points.Select(Clone));
        _sectionStart = -1;
        _sectionEnd = -1;
        _vm.Refresh();
        DrawRoute();
        UpdateHistoryButtons();
        UpdateSection();
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
            var result = TimeEditService.Apply(_vm.Activity.Points, _sectionStart, _sectionEnd, seconds ? value : null, seconds ? null : value);
            _undo.Push(snapshot);
            _redo.Clear();
            UpdateHistoryButtons();
            _vm.Refresh();
            UpdateSection();
            DrawRoute();
            StatusText.Text = $"Section {result.distanceKm:F2} km: {result.oldSeconds:F0}s → {result.newSeconds:F0}s. Export GPX or FIT to save.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to change section timing", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateHistoryButtons()
    {
        if (UndoButton is not null)
            UndoButton.IsEnabled = _undo.Count > 0;

        if (RedoButton is not null)
            RedoButton.IsEnabled = _redo.Count > 0;
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Activity is null || _undo.Count == 0)
            return;

        _redo.Push(Snapshot());
        Restore(_undo.Pop());
        StatusText.Text = "Undid route edit";
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Activity is null || _redo.Count == 0)
            return;

        _undo.Push(Snapshot());
        Restore(_redo.Pop());
        StatusText.Text = "Redid route edit";
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

            if (root.GetProperty("type").GetString() == "selection")
            {
                int a = root.GetProperty("start").GetInt32(), b = root.GetProperty("end").GetInt32();
                if (a >= 0 && b > a && b < _vm.Activity.Points.Count)
                {
                    _sectionStart = a;
                    _sectionEnd = b;
                    UpdateSection();
                    DrawRoute();
                }

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

            _undo.Push(Snapshot());
            _redo.Clear();
            RouteEditService.MoveMarker(_vm.Activity.Points, _markers, index, lat, lng);
            _vm.Refresh();
            UpdateHistoryButtons();
            DrawRoute();
            StatusText.Text = $"Reshaped route at point {index + 1}; export GPX or FIT to save";
        }
        catch (JsonException)
        {
        }
    }
}
