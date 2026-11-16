using System.Buffers.Binary;
using System.IO;
using JollyRouteViewer.Models;

namespace JollyRouteViewer.Services;

// Writes a new standards-based FIT activity, not a byte-for-byte rewrite of the source FIT.
// Record, lap, session and activity messages are regenerated from the edited GPS track.
public static class FitWriter
{
    private static readonly DateTimeOffset FitEpoch = new(1989, 12, 31, 0, 0, 0, TimeSpan.Zero);
    private readonly record struct Field(byte Number, byte Size, byte Type, long Value);

    private static Field U8(byte n, int v) => new(n, 1, 0x02, Math.Clamp(v, 0, 254));

    private static Field U16(byte n, double v) => new(n, 2, 0x84, (long)Math.Clamp(Math.Round(v), 0, 65534));

    private static Field U32(byte n, double v) => new(n, 4, 0x86, (long)Math.Clamp(Math.Round(v), 0, uint.MaxValue - 1d));

    private static Field S32(byte n, double v) => new(n, 4, 0x85, (long)Math.Clamp(Math.Round(v), int.MinValue + 1d, int.MaxValue));

    private static uint Timestamp(DateTimeOffset t) => checked((uint)(t.ToUniversalTime() - FitEpoch).TotalSeconds);

    private static int Semi(double degrees) => (int)Math.Clamp(Math.Round(degrees * 2147483648d / 180d), int.MinValue + 1d, int.MaxValue);

    private static byte Sport(string type) => type.ToLowerInvariant() switch
    { "running" or "run" => 1, "cycling" or "biking" or "bike" => 2, "swimming" => 5, "walking" or "hiking" => 11, _ => 0 };

    public static void Save(RouteActivity activity, string path)
    {
        var pts = activity.Points;
        if (pts.Count < 2 || pts.Any(p => p.Time is null))
            throw new InvalidOperationException("FIT export requires at least two GPS points and a timestamp on every point.");

        for (int i = 1; i < pts.Count; i++)
            if (pts[i].Time < pts[i - 1].Time)
                throw new InvalidOperationException("FIT timestamps must be chronological.");

        var first = pts[0].Time!.Value;
        var last = pts[^1].Time!.Value;
        double elapsed = Math.Max(0, (last - first).TotalSeconds);
        var cumulative = RouteMath.Cumulative(pts);
        double distance = cumulative[^1];
        double moving = 0, gain = 0, loss = 0;
        double? lastElevation = null;
        foreach (var p in pts)
        {
            if (p.Elevation is double ele)
            {
                if (lastElevation is double prev)
                {
                    gain += Math.Max(0, ele - prev);
                    loss += Math.Max(0, prev - ele);
                }

                lastElevation = ele;
            }
        }

        for (int i = 1; i < pts.Count; i++)
        {
            double dt = (pts[i].Time!.Value - pts[i - 1].Time!.Value).TotalSeconds;
            if (dt > 0 && dt <= 120 && RouteMath.Distance(pts[i - 1], pts[i]) / dt >= 0.5)
                moving += dt;
        }

        double avgSpeed = moving > 0 ? distance / moving : elapsed > 0 ? distance / elapsed : 0;
        double maxSpeed = pts.Where(p => p.Speed.HasValue).Select(p => p.Speed!.Value).DefaultIfEmpty(0).Max();
        int avgHr = Average(pts.Select(p => p.HeartRate));
        int avgCad = Average(pts.Select(p => p.Cadence));
        int avgPower = Average(pts.Select(p => p.Power));
        byte sport = Sport(activity.Type);
        using var data = new MemoryStream();
        void Message(byte local, ushort global, params Field[] fields) { data.WriteByte((byte)(0x40 | local)); data.WriteByte(0); data.WriteByte(0); Write16(data, global); data.WriteByte((byte)fields.Length); foreach (var f in fields) { data.WriteByte(f.Number); data.WriteByte(f.Size); data.WriteByte(f.Type); } data.WriteByte(local); foreach (var f in fields) { for (int b = 0; b < f.Size; b++) data.WriteByte((byte)(f.Value >> (8 * b))); } }

        Message(0, 0, U8(0, 4), U16(1, 255), U16(2, 1), U32(3, 1), U32(4, Timestamp(first)));
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            var fields = new List<Field>
            {
                U32(253, Timestamp(p.Time!.Value)),
                S32(0, Semi(p.Latitude)),
                S32(1, Semi(p.Longitude)),
                U32(5, cumulative[i] * 100)
            };

            if (p.Elevation is double e)
                fields.Add(U16(2, (e + 500) * 5));

            if (p.Speed is double speed)
                fields.Add(U16(6, speed * 1000));

            if (p.HeartRate is int hr)
                fields.Add(U8(3, hr));

            if (p.Cadence is int cad)
                fields.Add(U8(4, cad));

            if (p.Power is int pow)
                fields.Add(U16(7, pow));

            Message(1, 20, fields.ToArray());
        }

        var lap = new List<Field>
        {
            U32(253, Timestamp(last)),
            U32(2, Timestamp(first)),
            S32(3, Semi(pts[0].Latitude)),
            S32(4, Semi(pts[0].Longitude)),
            S32(5, Semi(pts[^1].Latitude)),
            S32(6, Semi(pts[^1].Longitude)),
            U32(7, elapsed * 1000),
            U32(8, moving * 1000),
            U32(9, distance * 100),
            U16(13, avgSpeed * 1000),
            U16(14, maxSpeed * 1000),
            U8(25, sport)
        };

        AddOptional(lap, 15, avgHr);
        AddOptional(lap, 17, avgCad);
        AddOptional16(lap, 19, avgPower);
        Message(2, 19, lap.ToArray());
        var session = new List<Field>
        {
            U32(253, Timestamp(last)),
            U32(2, Timestamp(first)),
            U32(7, elapsed * 1000),
            U32(8, moving * 1000),
            U32(9, distance * 100),
            U16(14, avgSpeed * 1000),
            U16(15, maxSpeed * 1000),
            U8(5, sport),
            U8(6, 0),
            U16(26, 1),
            U16(25, 0),
            U16(22, gain),
            U16(23, loss)
        };

        AddOptional(session, 16, avgHr);
        AddOptional(session, 18, avgCad);
        AddOptional16(session, 20, avgPower);
        Message(3, 18, session.ToArray());
        Message(4, 34, U32(253, Timestamp(last)), U32(0, moving * 1000), U16(1, 1), U8(2, 0), U8(3, 26), U8(4, 1));
        byte[] payload = data.ToArray();
        using var output = new MemoryStream();
        output.WriteByte(14);
        output.WriteByte(0x20);
        Write16(output, 100);
        Write32(output, (uint)payload.Length);
        output.Write(System.Text.Encoding.ASCII.GetBytes(".FIT"));
        byte[] header = output.ToArray();
        Write16(output, Crc(header));
        output.Write(payload);
        Write16(output, Crc(output.ToArray()));
        File.WriteAllBytes(path, output.ToArray());
    }

    private static void AddOptional(List<Field> fields, byte n, int value)
    {
        if (value > 0)
            fields.Add(U8(n, value));
    }

    private static void AddOptional16(List<Field> fields, byte n, int value)
    {
        if (value > 0)
            fields.Add(U16(n, value));
    }

    private static int Average(IEnumerable<int?> values)
    {
        var list = values.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        return list.Count == 0 ? 0 : (int)Math.Round(list.Average());
    }

    private static void Write16(Stream s, ushort v)
    {
        s.WriteByte((byte)v);
        s.WriteByte((byte)(v >> 8));
    }

    private static void Write32(Stream s, uint v)
    {
        for (int i = 0; i < 4; i++)
            s.WriteByte((byte)(v >> (8 * i)));
    }
    private static readonly ushort[] CrcTable = [0x0, 0xCC01, 0xD801, 0x1400, 0xF001, 0x3C00, 0x2800, 0xE401, 0xA001, 0x6C00, 0x7800, 0xB401, 0x5000, 0x9C01, 0x8801, 0x4400];

    private static ushort Crc(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (byte b in bytes)
        {
            ushort tmp = CrcTable[crc & 0xF];
            crc = (ushort)((crc >> 4) & 0x0FFF);
            crc ^= (ushort)(tmp ^ CrcTable[b & 0xF]);
            tmp = CrcTable[crc & 0xF];
            crc = (ushort)((crc >> 4) & 0x0FFF);
            crc ^= (ushort)(tmp ^ CrcTable[(b >> 4) & 0xF]);
        }

        return crc;
    }
}
