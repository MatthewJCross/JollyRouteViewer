namespace JollyRouteViewer.Models;
public sealed class RoutePoint
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double? Elevation { get; set; }
    public DateTimeOffset? Time { get; set; }
    public double? Speed { get; set; } // m/s
    public int? HeartRate { get; set; }
    public int? Cadence { get; set; }
    public int? Power { get; set; }
}
public sealed class RouteActivity
{
    public byte[]? OriginalFitBytes { get; set; }
    public string Name { get; set; } = "Activity";
    public string Type { get; set; } = "Unknown";
    public List<RoutePoint> Points { get; } = [];
}
