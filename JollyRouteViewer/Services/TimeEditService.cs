using JollyRouteViewer.Models;

namespace JollyRouteViewer.Services;

public static class TimeEditService
{
    public static (double distanceKm, double oldSeconds, double newSeconds) Apply(IList<RoutePoint> points, int start, int end, double? secondsSaved, double? targetKmh)
    {
        if (start < 0 || end >= points.Count || end <= start)
            throw new ArgumentException("Select a section containing at least two GPS points.");

        if (points.Any(p => p.Time is null))
            throw new InvalidOperationException("Every GPS point needs a timestamp to edit section speed.");

        var times = points.Select(p => p.Time!.Value).ToArray();
        for (int i = 1; i < times.Length; i++)
            if (times[i] < times[i - 1])
                throw new InvalidOperationException("GPS timestamps must be in chronological order.");

        double oldSeconds = (times[end] - times[start]).TotalSeconds;
        if (oldSeconds <= 0)
            throw new InvalidOperationException("Selected section has no elapsed time.");

        double distance = 0;
        for (int i = start + 1; i <= end; i++)
            distance += RouteMath.Distance(points[i - 1], points[i]);

        double newSeconds;
        if (secondsSaved.HasValue)
        {
            if (!double.IsFinite(secondsSaved.Value) || secondsSaved.Value <= 0)
                throw new ArgumentException("Enter a positive number of seconds to save.");

            newSeconds = oldSeconds - secondsSaved.Value;
        }
        else if (targetKmh.HasValue)
        {
            if (!double.IsFinite(targetKmh.Value) || targetKmh.Value <= 0)
                throw new ArgumentException("Enter a positive target average speed.");

            if (distance <= 0)
                throw new InvalidOperationException("Selected section has no distance.");

            newSeconds = distance / (targetKmh.Value / 3.6);
        }
        else
            throw new ArgumentException("Choose a time adjustment mode.");

        if (newSeconds < 1)
            throw new ArgumentException("The new section duration must be at least one second.");

        for (int i = start + 1; i <= end; i++)
        {
            double fraction = (times[i] - times[start]).TotalSeconds / oldSeconds;
            points[i].Time = times[start].AddSeconds(newSeconds * fraction);
        }

        var shift = TimeSpan.FromSeconds(newSeconds - oldSeconds);
        for (int i = end + 1; i < points.Count; i++)
            points[i].Time = times[i].Add(shift);

        for (int i = start + 1; i <= end; i++)
        {
            double dt = (points[i].Time!.Value - points[i - 1].Time!.Value).TotalSeconds;
            points[i].Speed = dt > 0 ? RouteMath.Distance(points[i - 1], points[i]) / dt : null;
        }

        return (distance / 1000, oldSeconds, newSeconds);
    }
}
