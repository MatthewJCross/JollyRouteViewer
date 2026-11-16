using System.ComponentModel;
using System.Runtime.CompilerServices;
using JollyRouteViewer.Models;
using JollyRouteViewer.Services;
namespace JollyRouteViewer.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private RouteActivity? _activity;
    private bool _allPoints;
    private double _spacingKm = 1;
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public RouteActivity? Activity
    {
        get => _activity; set
        {
            _activity = value;
            Notify();
            Refresh();
        }
    }
    public bool AllPoints
    {
        get => _allPoints; set
        {
            _allPoints = value;
            Notify();
            Notify(nameof(MarkerCount));
        }
    }
    public double SpacingKm
    {
        get => _spacingKm; set
        {
            _spacingKm = Math.Max(.1, value);
            Notify();
            Notify(nameof(MarkerCount));
        }
    }
    public string Name => Activity?.Name ?? "No activity loaded";
    public string Type => Activity?.Type ?? "—";
    public string Distance => Activity?.Points.Count > 1 ? $"{RouteMath.Cumulative(Activity.Points)[^1] / 1000:F2} km" : "—";
    public string Elapsed => FormatDuration(GetElapsed());
    public string Moving => FormatDuration(GetMoving());
    public string AverageSpeed
    {
        get
        {
            if (Activity?.Points.Count < 2)
                return "—";

            double hours = GetMoving().TotalHours;
            return hours > 0 ? $"{RouteMath.Cumulative(Activity.Points)[^1] / 1000 / hours:F1} km/h" : "—";
        }
    }

    public string AveragePace
    {
        get
        {
            var points = Activity?.Points;
            if (points is null || points.Count < 2)
                return "—";

            double km = RouteMath.Cumulative(points)[^1] / 1000.0;
            double minutes = GetMoving().TotalMinutes;
            if (km <= 0 || minutes <= 0)
                return "—";

            var pace = TimeSpan.FromMinutes(minutes / km);
            return $"{(int)pace.TotalMinutes}:{pace.Seconds:00} /km";
        }
    }

    public string MaxSpeed => Activity?.Points.Where(p => p.Speed.HasValue).Select(p => p.Speed!.Value).DefaultIfEmpty(-1).Max() is double v && v >= 0 ? $"{v * 3.6:F1} km/h" : "—";
    public string ElevationGain => Activity is null ? "—" : $"{Elevation(true):F0} m";
    public string ElevationLoss => Activity is null ? "—" : $"{Elevation(false):F0} m";
    public string HeartRate => Average(p => p.HeartRate) is double v ? $"{v:F0} bpm" : "—";
    public string Cadence => Average(p => p.Cadence) is double v ? $"{v:F0} rpm" : "—";
    public string Power => Average(p => p.Power) is double v ? $"{v:F0} W" : "—";
    public string Start => Activity?.Points.FirstOrDefault(p => p.Time.HasValue)?.Time?.ToLocalTime().ToString("g") ?? "—";
    public int MarkerCount => Activity is null ? 0 : RouteMath.MarkerIndices(Activity.Points, SpacingKm * 1000, AllPoints).Count;

    private double? Average(Func<RoutePoint, int?> getter)
    {
        var vals = Activity?.Points.Select(getter).Where(x => x.HasValue).Select(x => (double)x!.Value).ToList();
        return vals?.Count > 0 ? vals.Average() : null;
    }

    private double Elevation(bool gain)
    {
        double total = 0;
        double? prev = null;
        foreach (var p in Activity!.Points)
        {
            if (p.Elevation is not double e)
                continue;

            if (prev.HasValue)
            {
                double d = e - prev.Value;
                if ((gain && d > 0) || (!gain && d < 0))
                    total += Math.Abs(d);
            }

            prev = e;
        }

        return total;
    }

    private TimeSpan GetElapsed()
    {
        var points = Activity?.Points;
        if (points is null || points.Count < 2)
            return TimeSpan.Zero;

        DateTimeOffset? start = points[0].Time;
        DateTimeOffset? end = points[^1].Time;
        if (!start.HasValue || !end.HasValue)
            return TimeSpan.Zero;

        TimeSpan elapsed = end.Value - start.Value;
        return elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero;
    }

    private TimeSpan GetMoving()
    {
        var p = Activity?.Points;
        if (p is null)
            return TimeSpan.Zero;

        double seconds = 0;
        for (int i = 1; i < p.Count; i++)
        {
            if (p[i].Time is null || p[i - 1].Time is null)
                continue;

            double dt = (p[i].Time!.Value - p[i - 1].Time!.Value).TotalSeconds;
            if (dt <= 0 || dt > 120)
                continue;

            double speed = RouteMath.Distance(p[i - 1], p[i]) / dt;
            if (speed >= 0.5)
                seconds += dt;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static string FormatDuration(TimeSpan t) => t == TimeSpan.Zero ? "—" : t.ToString(@"hh\:mm\:ss");

    public void Refresh()
    {
        foreach (var prop in new[]
        {
            nameof(Name),
            nameof(Type),
            nameof(Distance),
            nameof(Elapsed),
            nameof(Moving),
            nameof(AverageSpeed),
            nameof(AveragePace),
            nameof(MaxSpeed),
            nameof(ElevationGain),
            nameof(ElevationLoss),
            nameof(HeartRate),
            nameof(Cadence),
            nameof(Power),
            nameof(Start),
            nameof(MarkerCount)
        })
            Notify(prop);
    }
}
