namespace JollyRouteViewer.Models
{
    public sealed class RoutePoint
    {
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public double? Elevation { get; set; }
        public DateTimeOffset? Time { get; set; }
        public double? Distance { get; set; } // metres, recorded cumulative distance for indoor activities
        public double? Speed { get; set; } // m/s
        public int? HeartRate { get; set; }
        public int? Cadence { get; set; }
        public int? Power { get; set; }
    }
    public sealed class SwimLength
    {
        public DateTimeOffset? StartTime { get; set; }
        public DateTimeOffset? EndTime { get; set; }
        public double ElapsedSeconds { get; set; }
        public double TimerSeconds { get; set; }
        public int? Strokes { get; set; }
        public int? StrokeType { get; set; }
        public bool IsRest { get; set; }
    }
    public sealed class RouteActivity
    {
        public byte[]? OriginalFitBytes { get; set; }
        public string Name { get; set; } = "Activity";
        public string Type { get; set; } = "Unknown";
        public List<RoutePoint> Points { get; } = [];
        public double? PoolLengthMetres { get; set; }
        public List<SwimLength> SwimLengths { get; } = [];
        public bool IsSwimming => Type is "Pool Swimming" or "Open Water Swimming" or "Swimming";

    }
}