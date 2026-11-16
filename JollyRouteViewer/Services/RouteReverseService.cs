using JollyRouteViewer.Models;
namespace JollyRouteViewer.Services;
public static class RouteReverseService
{
    public static void Reverse(RouteActivity activity)
    {
        var pts = activity.Points;
        if (pts.Count < 2) 
            return;

        var oldTimes = pts.Select(p => p.Time).ToArray();
        pts.Reverse();
        // Retain original start/end and reverse the sequence of time intervals.
        // Reversing recorded samples also reverses HR/cadence/power/elevation.
        if (oldTimes.All(t => t.HasValue))
        {
            DateTimeOffset start = oldTimes[0]!.Value;
            DateTimeOffset end = oldTimes[^1]!.Value;
            for (int i = 0; i < pts.Count; i++)
                pts[i].Time = start + (end - oldTimes[pts.Count - 1 - i]!.Value);
        }
        else
        {
            // Partial timestamps are ambiguous; retain the available values on their reversed samples.
        }
        RecalculateSpeeds(pts);
    }
    public static void RecalculateSpeeds(IList<RoutePoint> pts)
    {
        if (pts.Count == 0) return;
        pts[0].Speed = null;
        for (int i = 1; i < pts.Count; i++)
        {
            double dt = pts[i].Time is DateTimeOffset a && pts[i-1].Time is DateTimeOffset b ? (a-b).TotalSeconds : 0;
            pts[i].Speed = dt > 0 ? RouteMath.Distance(pts[i-1], pts[i]) / dt : null;
        }
    }
}
