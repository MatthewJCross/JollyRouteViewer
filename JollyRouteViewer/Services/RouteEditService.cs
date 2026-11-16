using JollyRouteViewer.Models;
namespace JollyRouteViewer.Services;

public static class RouteEditService
{
    // Smoothly distribute a marker displacement between neighbouring control markers.
    // Endpoints remain fixed, except when the dragged marker itself is an endpoint.
    public static void MoveMarker(IList<RoutePoint> points, IReadOnlyList<int> controls, int index, double latitude, double longitude)
    {
        if (index < 0 || index >= points.Count || !controls.Contains(index)) 
            return;

        double deltaLat = latitude - points[index].Latitude;
        double deltaLon = longitude - points[index].Longitude;
        int controlPosition = controls.IndexOfValue(index);
        int left = controlPosition > 0 ? controls[controlPosition - 1] : index;
        int right = controlPosition + 1 < controls.Count ? controls[controlPosition + 1] : index;
        for (int i = left; i <= right; i++)
        {
            double weight;
            if (i == index) weight = 1;
            else if (i < index && left < index)
            {
                double t = (double)(i - left) / (index - left);
                weight = t * t * (3 - 2 * t);
            }
            else if (i > index && right > index)
            {
                double t = (double)(right - i) / (right - index);
                weight = t * t * (3 - 2 * t);
            }
            else weight = 0;
            points[i].Latitude = Math.Clamp(points[i].Latitude + deltaLat * weight, -90, 90);
            points[i].Longitude = NormalizeLongitude(points[i].Longitude + deltaLon * weight);
        }
    }
    private static double NormalizeLongitude(double lon) => ((lon + 180) % 360 + 360) % 360 - 180;
    private static int IndexOfValue(this IReadOnlyList<int> values, int value)
    {
        for (int i = 0; i < values.Count; i++) 
            if (values[i] == value) 
                return i;

        return -1;
    }
}
