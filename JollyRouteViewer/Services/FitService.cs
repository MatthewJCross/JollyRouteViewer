using System.Buffers.Binary;
using System.IO;
using JollyRouteViewer.Models;
namespace JollyRouteViewer.Services;
// FIT record decoder for standard activity/record fields; handles local definitions and compressed timestamps.
public static class FitService
{
    private record Field(byte Number, byte Size, byte BaseType);
    private record Definition(ushort Global, bool BigEndian, List<Field> Fields, List<Field> DeveloperFields);

    public static RouteActivity Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 14)
            throw new InvalidDataException("Invalid FIT header");

        int headerSize = bytes[0];
        if (headerSize < 12 || bytes.Length < headerSize + 2 || System.Text.Encoding.ASCII.GetString(bytes, 8, 4) != ".FIT")
            throw new InvalidDataException("Not a FIT file");

        int dataSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4));
        int end = checked(headerSize + dataSize);
        if (end > bytes.Length - 2)
            throw new InvalidDataException("Truncated FIT data");

        var defs = new Definition?[16];
        var activity = new RouteActivity
        {
            Name = Path.GetFileNameWithoutExtension(path),
            OriginalFitBytes = bytes
        };

        uint? lastTimestamp = null;
        int pos = headerSize;
        while (pos < end)
        {
            byte h = bytes[pos++];
            bool compressed = (h & 0x80) != 0;
            int local = compressed ? (h >> 5) & 3 : h & 15;
            if (!compressed && (h & 0x40) != 0)
            {
                bool developer = (h & 0x20) != 0;
                Require(6);
                pos++;
                bool big = bytes[pos++] != 0;
                ushort global = big ? BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(pos, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos, 2));
                pos += 2;
                int count = bytes[pos++];
                Require(count * 3);
                var fields = new List<Field>();
                for (int i = 0; i < count; i++)
                {
                    fields.Add(new Field(bytes[pos], bytes[pos + 1], bytes[pos + 2]));
                    pos += 3;
                }

                var dev = new List<Field>();
                if (developer)
                {
                    Require(1);
                    int n = bytes[pos++];
                    Require(n * 3);
                    for (int i = 0; i < n; i++)
                    {
                        dev.Add(new Field(bytes[pos], bytes[pos + 1], bytes[pos + 2]));
                        pos += 3;
                    }
                }

                defs[local] = new Definition(global, big, fields, dev);
                continue;
            }

            var def = defs[local] ?? throw new InvalidDataException("FIT data message has no definition");
            var values = new Dictionary<int, double>();
            foreach (var field in def.Fields)
            {
                Require(field.Size);
                var data = bytes.AsSpan(pos, field.Size);
                pos += field.Size;
                if (TryNumber(data, field.BaseType, def.BigEndian, out double v))
                    values[field.Number] = v;
            }

            foreach (var field in def.DeveloperFields)
            {
                Require(field.Size);
                pos += field.Size;
            }

            if (compressed)
            {
                uint offset = (uint)(h & 31);
                if (lastTimestamp.HasValue)
                {
                    uint ts = (lastTimestamp.Value & ~31u) | offset;
                    if (ts < lastTimestamp.Value)
                        ts += 32;

                    lastTimestamp = ts;
                    if (!values.ContainsKey(253))
                        values[253] = ts;
                }
            }

            if (values.TryGetValue(253, out double timestamp))
                lastTimestamp = (uint)timestamp;

            if (def.Global == 0 && values.TryGetValue(0, out double sport))
                activity.Type = sport switch { 1 => "Running", 2 => "Cycling", 5 => "Swimming", 11 => "Walking", _ => "Other" };

            if (def.Global == 18 && values.TryGetValue(5, out double sessionSport))
            {
                int sportCode = (int)sessionSport;
                int subSportCode = values.TryGetValue(6, out double sub) ? (int)sub : 0;
                activity.Type = (sportCode, subSportCode) switch
                {
                    (2, 6) => "Indoor Cycling",
                    (2, 58) => "Virtual Cycling",
                    (1, 1) => "Treadmill Running",
                    (1, 58) => "Virtual Running",
                    (15, 14) => "Indoor Rowing",
                    (15, _) => "Rowing",
                    (1, _) => "Running",
                    (2, _) => "Cycling",
                    (11, _) => "Walking",
                    _ => activity.Type
                };
            }

            if (def.Global == 18 && values.TryGetValue(44, out double poolLength))
                activity.PoolLengthMetres = poolLength / 100;

            if (def.Global == 18 && values.TryGetValue(5, out double swimSport) && (int)swimSport == 5)
                activity.Type = values.TryGetValue(6, out double swimSub) && (int)swimSub == 17
                    ? "Open Water Swimming" : "Pool Swimming";

            if (def.Global == 101)
            {
                var length = new SwimLength
                {
                    StartTime = values.TryGetValue(2, out double st) ? DateTimeOffset.FromUnixTimeSeconds((long)st + 631065600L) : null,
                    EndTime = values.TryGetValue(253, out double et) ? DateTimeOffset.FromUnixTimeSeconds((long)et + 631065600L) : null,
                    ElapsedSeconds = values.TryGetValue(3, out double el) ? el / 1000 : 0,
                    TimerSeconds = values.TryGetValue(4, out double tm) ? tm / 1000 : 0,
                    Strokes = values.TryGetValue(5, out double strokes) ? (int)strokes : null,
                    StrokeType = values.TryGetValue(7, out double stroke) ? (int)stroke : null,
                    IsRest = values.TryGetValue(12, out double kind) && (int)kind == 0
                };
                activity.SwimLengths.Add(length);
            }

            if (def.Global != 20)
                continue;

            var p = new RoutePoint
            {
                Latitude = values.TryGetValue(0, out double lat) ? lat * 180 / 2147483648d : null,
                Longitude = values.TryGetValue(1, out double lon) ? lon * 180 / 2147483648d : null
            };

            if (values.TryGetValue(5, out double recordedDistance))
                p.Distance = recordedDistance / 100;

            if (values.TryGetValue(2, out double alt))
                p.Elevation = alt / 5 - 500;

            if (values.TryGetValue(78, out double enhancedAlt))
                p.Elevation = enhancedAlt / 5 - 500;

            if (values.TryGetValue(6, out double speed))
                p.Speed = speed / 1000;

            if (values.TryGetValue(73, out double enhancedSpeed))
                p.Speed = enhancedSpeed / 1000;

            if (values.TryGetValue(3, out double hr))
                p.HeartRate = (int)hr;

            if (values.TryGetValue(4, out double cadence))
                p.Cadence = (int)cadence;

            if (values.TryGetValue(7, out double power))
                p.Power = (int)power;

            if (values.TryGetValue(253, out double t))
                p.Time = DateTimeOffset.FromUnixTimeSeconds((long)t + 631065600L);

            if ((!p.Latitude.HasValue && !p.Longitude.HasValue) || (p.Latitude is double plat && p.Longitude is double plon && Math.Abs(plat) <= 90 && Math.Abs(plon) <= 180))
                activity.Points.Add(p);
        }

        // Pool FIT files can contain length messages but no regular record messages.
        // Construct a timeline from the lengths without inventing GPS positions.
        if (activity.Points.Count == 0 && activity.SwimLengths.Count > 0)
        {
            double metres = 0;
            foreach (var length in activity.SwimLengths)
            {
                if (!length.IsRest && activity.PoolLengthMetres is double pool)
                    metres += pool;
                activity.Points.Add(new RoutePoint { Time = length.EndTime, Distance = metres });
            }
            if (activity.SwimLengths[0].StartTime is DateTimeOffset start)
                activity.Points.Insert(0, new RoutePoint { Time = start, Distance = 0 });
        }
        return activity;
        void Require(int n)
        {
            if (n < 0 || pos + n > end)
                throw new InvalidDataException("Truncated FIT message");
        }
    }

    private static bool TryNumber(ReadOnlySpan<byte> data, byte type, bool big, out double value)
    {
        value = 0;
        int t = type & 31;
        int len = t switch { 0 or 1 or 2 or 10 or 13 => 1, 3 or 4 or 11 => 2, 5 or 6 or 8 or 12 => 4, 7 => 0, 9 or 14 or 15 or 16 => 8, _ => 0 };
        if (len == 0 || data.Length < len)
            return false;

        ulong u = len switch { 1 => data[0], 2 => big ? BinaryPrimitives.ReadUInt16BigEndian(data) : BinaryPrimitives.ReadUInt16LittleEndian(data), 4 => big ? BinaryPrimitives.ReadUInt32BigEndian(data) : BinaryPrimitives.ReadUInt32LittleEndian(data), 8 => big ? BinaryPrimitives.ReadUInt64BigEndian(data) : BinaryPrimitives.ReadUInt64LittleEndian(data), _ => 0 };
        ulong invalid = t switch
        {
            0 => 0xFF,
            1 => 0x7F,
            2 or 10 or 13 => 0xFF,
            3 => 0x7FFF,
            4 or 11 => 0xFFFF,
            5 => 0x7FFFFFFF,
            6 or 12 => 0xFFFFFFFF,
            9 => 0x7FFFFFFFFFFFFFFF,
            14 or 15 or 16 => ulong.MaxValue,
            _ => ulong.MaxValue
        };

        if (u == invalid)
            return false;

        value = t switch
        {
            1 => (sbyte)u,
            3 => (short)u,
            5 => (int)u,
            9 => (long)u,
            8 => BitConverter.Int32BitsToSingle((int)u),
            15 => BitConverter.Int64BitsToDouble((long)u),
            _ => u
        };

        return double.IsFinite(value);
    }
}
