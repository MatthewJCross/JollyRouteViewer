using System.Globalization;
using System.IO;
using System.Xml.Linq;
using JollyRouteViewer.Models;
namespace JollyRouteViewer.Services;

public static class GpxService
{
    public static RouteActivity Load(string path)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root ?? throw new InvalidDataException("Empty GPX");
        XNamespace ns = root.Name.Namespace;
        var activity = new RouteActivity
        {
            Name = Path.GetFileNameWithoutExtension(path)
        };

        activity.Name = (string?)root.Element(ns + "metadata")?.Element(ns + "name") ?? (string?)root.Descendants(ns + "trk").FirstOrDefault()?.Element(ns + "name") ?? activity.Name;
        activity.Type = (string?)root.Descendants(ns + "trk").FirstOrDefault()?.Element(ns + "type") ?? "Unknown";
        var nodes = root.Descendants(ns + "trkpt").Concat(root.Descendants(ns + "rtept"));
        foreach (var node in nodes)
        {
            if (!double.TryParse((string?)node.Attribute("lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) || !double.TryParse((string?)node.Attribute("lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                continue;

            var p = new RoutePoint
            {
                Latitude = lat,
                Longitude = lon
            };

            if (double.TryParse((string?)node.Element(ns + "ele"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ele))
                p.Elevation = ele;

            if (DateTimeOffset.TryParse((string?)node.Element(ns + "time"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
                p.Time = time;

            foreach (var ext in node.Descendants())
            {
                if (int.TryParse(ext.Value, out int v))
                {
                    if (ext.Name.LocalName.Equals("hr", StringComparison.OrdinalIgnoreCase))
                        p.HeartRate = v;

                    if (ext.Name.LocalName.Equals("cad", StringComparison.OrdinalIgnoreCase))
                        p.Cadence = v;

                    if (ext.Name.LocalName.Equals("power", StringComparison.OrdinalIgnoreCase))
                        p.Power = v;
                }
            }

            var speedNode = node.Descendants().FirstOrDefault(x => x.Name.LocalName.Equals("speed", StringComparison.OrdinalIgnoreCase));
            if (double.TryParse(speedNode?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
                p.Speed = speed;

            activity.Points.Add(p);
        }

        return activity;
    }

    public static void Save(RouteActivity activity, string path)
    {
        XNamespace ns = "http://www.topografix.com/GPX/1/1";
        XNamespace gpxtpx = "http://www.garmin.com/xmlschemas/TrackPointExtension/v1";
        XNamespace gpxx = "http://www.garmin.com/xmlschemas/GpxExtensions/v3";
        var trk = new XElement(ns + "trk", new XElement(ns + "name", activity.Name), new XElement(ns + "type", activity.Type), new XElement(ns + "trkseg", activity.Points.Select(p => new XElement(ns + "trkpt", new XAttribute("lat", p.Latitude.ToString("R", CultureInfo.InvariantCulture)), new XAttribute("lon", p.Longitude.ToString("R", CultureInfo.InvariantCulture)), p.Elevation is double e ? new XElement(ns + "ele", e.ToString("R", CultureInfo.InvariantCulture)) : null, p.Time is DateTimeOffset t ? new XElement(ns + "time", t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)) : null, p.HeartRate.HasValue || p.Cadence.HasValue || p.Power.HasValue || p.Speed.HasValue ? new XElement(ns + "extensions", new XElement(gpxtpx + "TrackPointExtension", p.HeartRate is int hr ? new XElement(gpxtpx + "hr", hr) : null, p.Cadence is int cad ? new XElement(gpxtpx + "cad", cad) : null), p.Power is int power ? new XElement("power", power) : null, p.Speed is double speed ? new XElement("speed", speed.ToString("R", CultureInfo.InvariantCulture)) : null) : null))));
        new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement(ns + "gpx", new XAttribute("version", "1.1"), new XAttribute("creator", "JollyRouteViewer"), new XAttribute(XNamespace.Xmlns + "gpxtpx", gpxtpx), new XAttribute(XNamespace.Xmlns + "gpxx", gpxx), trk)).Save(path);
    }
}
