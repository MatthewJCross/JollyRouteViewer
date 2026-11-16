using JollyRouteViewer.Models;
namespace JollyRouteViewer.Services;
public static class RouteMath
{
    public static double Distance(RoutePoint a, RoutePoint b)
    {
        const double r = 6371000;
        double dLat = (b.Latitude-a.Latitude)*Math.PI/180, dLon = (b.Longitude-a.Longitude)*Math.PI/180;
        double x = Math.Sin(dLat/2)*Math.Sin(dLat/2) + Math.Cos(a.Latitude*Math.PI/180)*Math.Cos(b.Latitude*Math.PI/180)*Math.Sin(dLon/2)*Math.Sin(dLon/2);
        return 2*r*Math.Atan2(Math.Sqrt(x), Math.Sqrt(Math.Max(0,1-x)));
    }
    public static double[] Cumulative(IReadOnlyList<RoutePoint> p)
    {
        var result = new double[p.Count];
        for(int i=1;i<p.Count;i++) 
            result[i]=result[i-1]+Distance(p[i-1],p[i]);

        return result;
    }
    public static List<int> MarkerIndices(IReadOnlyList<RoutePoint> points, double spacingMetres, bool all)
    {
        var indices = new List<int>();
        if(points.Count==0)
            return indices;

        if(all) 
            return Enumerable.Range(0,points.Count).ToList();

        var distances=Cumulative(points);
        indices.Add(0);
        for(double target=spacingMetres;target<distances[^1];target+=spacingMetres)
        {
            int i=Array.BinarySearch(distances,target);
            if(i<0)
                i=~i;

            i=Math.Clamp(i,0,points.Count-1);
            if(indices[^1]!=i) 
                indices.Add(i);
        }
        if(indices[^1]!=points.Count-1) 
            indices.Add(points.Count-1);

        return indices;
    }
}
