using System.Buffers.Binary;
using System.IO;
using JollyRouteViewer.Models;

namespace JollyRouteViewer.Services;

/// <summary>Edits standard fields in the original FIT byte stream, retaining all other
/// messages, developer definitions, unknown fields, and device metadata verbatim.
/// Fails closed when a source layout cannot represent the requested edits.</summary>
public static class FitPreservingWriter
{
    private record Field(byte Number, int Offset, byte Size, byte Type);
    private record Definition(ushort Global, bool Big, List<Field> Fields, int Size);
    private static readonly DateTimeOffset Epoch = new(1989, 12, 31, 0, 0, 0, TimeSpan.Zero);

    private static uint Seconds(DateTimeOffset time) => checked((uint)Math.Floor((time.ToUniversalTime() - Epoch).TotalSeconds));

    private static int Semi(double degrees) => checked((int)Math.Round(degrees * 2147483648d / 180d));

    public static void Save(RouteActivity activity, string path)
    {
        if (activity.OriginalFitBytes is null)
            throw new InvalidOperationException("No original FIT data is available.");

        var bytes = (byte[])activity.OriginalFitBytes.Clone();
        if (bytes.Length < 14)
            throw new InvalidDataException("Invalid FIT file.");

        int header = bytes[0];
        int end = checked(header + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
        if (header < 12 || end + 2 > bytes.Length)
            throw new InvalidDataException("Invalid FIT data length.");

        var defs = new Definition?[16];
        var cumulative = RouteMath.Cumulative(activity.Points);
        double distance = cumulative.Length == 0 ? 0 : cumulative[^1];
        var pts = activity.Points;
        if (pts.Count < 2 || pts.Any(p => !p.Time.HasValue))
            throw new InvalidOperationException("FIT editing requires timestamped GPS points.");

        for (int i = 1; i < pts.Count; i++)
            if (pts[i].Time < pts[i - 1].Time)
                throw new InvalidOperationException("Timestamps must be chronological.");

        double elapsed = (pts[^1].Time!.Value - pts[0].Time!.Value).TotalSeconds;
        double moving = 0, maxSpeed = 0, gain = 0, loss = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            double dt = (pts[i].Time!.Value - pts[i - 1].Time!.Value).TotalSeconds;
            double speed = dt > 0 ? RouteMath.Distance(pts[i - 1], pts[i]) / dt : 0;
            maxSpeed = Math.Max(maxSpeed, speed);
            if (dt > 0 && dt <= 120 && speed >= 0.5)
                moving += dt;

            if (pts[i].Elevation is double a && pts[i - 1].Elevation is double b)
            {
                gain += Math.Max(0, a - b);
                loss += Math.Max(0, b - a);
            }
        }

        double average = moving > 0 ? distance / moving : elapsed > 0 ? distance / elapsed : 0;
        int record = 0, position = header;
        uint? previousWrittenTimestamp = null;
        uint? previousTimestamp = null;
        while (position < end)
        {
            byte h = bytes[position++];
            bool compressed = (h & 128) != 0;
            int local = compressed ? (h >> 5) & 3 : h & 15;
            if (!compressed && (h & 64) != 0)
            {
                bool hasDev = (h & 32) != 0;
                Check(position, 5, end);
                bool big = bytes[position + 1] != 0;
                ushort global = big ? BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(position + 2, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 2, 2));
                int count = bytes[position + 4];
                position += 5;
                Check(position, count * 3, end);
                var fields = new List<Field>();
                int offset = 0;
                for (int i = 0; i < count; i++)
                {
                    byte number = bytes[position++], size = bytes[position++], type = bytes[position++];
                    fields.Add(new Field(number, offset, size, type));
                    offset += size;
                }

                if (hasDev)
                {
                    Check(position, 1, end);
                    int devCount = bytes[position++];
                    Check(position, devCount * 3, end);
                    for (int i = 0; i < devCount; i++)
                    {
                        position++;
                        offset += bytes[position++];
                        position++;
                    }
                }

                defs[local] = new Definition(global, big, fields, offset);
                continue;
            }

            var def = defs[local] ?? throw new InvalidDataException("FIT data without definition.");
            Check(position, def.Size, end);
            int start = position;
            position += def.Size;
            var timestampField = def.Fields.FirstOrDefault(f => f.Number == 253);
            uint? timestamp = timestampField is not null ? ReadU32(bytes, start + timestampField.Offset, timestampField, def.Big) : null;
            if (compressed && previousTimestamp.HasValue)
            {
                uint old = (previousTimestamp.Value & ~31u) | (uint)(h & 31);
                if (old < previousTimestamp.Value)
                    old += 32;

                timestamp ??= old;
            }

            if (timestamp.HasValue)
                previousTimestamp = timestamp;

            if (def.Global == 20)
            {
                var lat = def.Fields.FirstOrDefault(f => f.Number == 0);
                var lon = def.Fields.FirstOrDefault(f => f.Number == 1);
                if (lat is null || lon is null || lat.Size != 4 || lon.Size != 4)
                    continue;

                if (!ValidCoordinate(bytes, start, lat, lon, def.Big))
                    continue;

                if (record >= pts.Count)
                    throw new InvalidOperationException("FIT record count changed.");

                var p = pts[record];
                Patch(def, bytes, start, 0, Semi(p.Latitude));
                Patch(def, bytes, start, 1, Semi(p.Longitude));
                Patch(def, bytes, start, 5, cumulative[record] * 100);
                if (p.Elevation.HasValue)
                {
                    Patch(def, bytes, start, 2, (p.Elevation.Value + 500) * 5);
                    Patch(def, bytes, start, 78, (p.Elevation.Value + 500) * 5);
                }

                if (p.Speed.HasValue)
                {
                    Patch(def, bytes, start, 6, p.Speed.Value * 1000);
                    Patch(def, bytes, start, 73, p.Speed.Value * 1000);
                }

                if (p.HeartRate.HasValue)
                    Patch(def, bytes, start, 3, p.HeartRate.Value);

                if (p.Cadence.HasValue)
                    Patch(def, bytes, start, 4, p.Cadence.Value);

                if (p.Power.HasValue)
                    Patch(def, bytes, start, 7, p.Power.Value);

                uint newTime = Seconds(p.Time!.Value);
                if (compressed && timestampField is null)
                {
                    if (!previousWrittenTimestamp.HasValue || ((previousWrittenTimestamp.Value & ~31u) | (newTime & 31u)) + (newTime % 32 < previousWrittenTimestamp.Value % 32 ? 32u : 0u) != newTime)
                        throw new InvalidOperationException("Edited timestamps cannot be encoded in this FIT file's compressed timestamp layout. Use regenerated FIT export instead.");

                    bytes[start - 1] = (byte)((h & 0xE0) | (newTime & 31));
                }
                else if (timestampField is null)
                    throw new InvalidOperationException("A GPS record lacks an editable timestamp field. Use regenerated FIT export instead.");
                else
                    Patch(def, bytes, start, 253, newTime);
                previousWrittenTimestamp = newTime;
                record++;
            }
            else if (def.Global == 19 || def.Global == 18)
            {
                Patch(def, bytes, start, 253, Seconds(pts[^1].Time!.Value));
                Patch(def, bytes, start, 2, Seconds(pts[0].Time!.Value));
                Patch(def, bytes, start, 7, elapsed * 1000);
                Patch(def, bytes, start, 8, moving * 1000);
                Patch(def, bytes, start, 9, distance * 100);
                Patch(def, bytes, start, def.Global == 19 ? 13 : 14, average * 1000);
                Patch(def, bytes, start, def.Global == 19 ? 14 : 15, maxSpeed * 1000);
                if (def.Global == 18)
                {
                    Patch(def, bytes, start, 22, gain);
                    Patch(def, bytes, start, 23, loss);
                }
                else
                {
                    Patch(def, bytes, start, 3, Semi(pts[0].Latitude));
                    Patch(def, bytes, start, 4, Semi(pts[0].Longitude));
                    Patch(def, bytes, start, 5, Semi(pts[^1].Latitude));
                    Patch(def, bytes, start, 6, Semi(pts[^1].Longitude));
                }
            }
            else if (def.Global == 34)
            {
                Patch(def, bytes, start, 253, Seconds(pts[^1].Time!.Value));
                Patch(def, bytes, start, 0, moving * 1000);
            }
        }

        if (record != pts.Count)
            throw new InvalidOperationException($"FIT record mismatch: original {record}, edited {pts.Count}.");

        ushort crc = Crc(bytes.AsSpan(0, end));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end, 2), crc);
        File.WriteAllBytes(path, bytes);
    }

    private static bool ValidCoordinate(byte[] data, int start, Field lat, Field lon, bool big)
    {
        int a = big ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(start + lat.Offset, 4)) : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(start + lat.Offset, 4));
        int b = big ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(start + lon.Offset, 4)) : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(start + lon.Offset, 4));
        return a != int.MaxValue && b != int.MaxValue && Math.Abs(a * 180d / 2147483648d) <= 90;
    }

    private static uint? ReadU32(byte[] data, int pos, Field f, bool big) => f.Size == 4 ? (big ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4))) : null;

    private static void Patch(Definition def, byte[] data, int start, int number, double value)
    {
        var f = def.Fields.FirstOrDefault(x => x.Number == number);
        if (f is null)
            return;

        int type = f.Type & 31;
        int offset = start + f.Offset;
        if (!double.IsFinite(value))
            return;

        switch (type)
        {
            case 0:
            case 2:
            case 10:
            case 13:
                if (f.Size == 1)
                    data[offset] = (byte)Math.Clamp(Math.Round(value), 0, 254); break;
            case 1:
                if (f.Size == 1)
                    data[offset] = unchecked((byte)(sbyte)Math.Clamp(Math.Round(value), -128, 126)); break;
            case 3:
                if (f.Size == 2)
                    Write16(unchecked((ushort)(short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue - 1))); break;
            case 4:
            case 11:
                if (f.Size == 2)
                    Write16((ushort)Math.Clamp(Math.Round(value), 0, 65534)); break;
            case 5:
                if (f.Size == 4)
                    Write32(unchecked((uint)(int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue - 1))); break;
            case 6:
            case 12:
                if (f.Size == 4)
                    Write32((uint)Math.Clamp(Math.Round(value), 0, uint.MaxValue - 1d)); break;
        }

        void Write16(ushort v) 
        { 
            if (def.Big) 
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset, 2), v); 
            else 
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), v); 
        }

        void Write32(uint v) 
        { 
            if (def.Big) 
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), v); 
            else 
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), v);
        }
    }

    private static void Check(int pos, int size, int end)
    {
        if (size < 0 || pos + size > end)
            throw new InvalidDataException("Truncated FIT message.");
    }
    private static readonly ushort[] Table = [0, 0xCC01, 0xD801, 0x1400, 0xF001, 0x3C00, 0x2800, 0xE401, 0xA001, 0x6C00, 0x7800, 0xB401, 0x5000, 0x9C01, 0x8801, 0x4400];

    private static ushort Crc(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (byte b in bytes)
        {
            ushort t = Table[crc & 15];
            crc = (ushort)(((crc >> 4) & 0x0FFF) ^ (t ^ Table[b & 15]));
            t = Table[crc & 15];
            crc = (ushort)(((crc >> 4) & 0x0FFF) ^ (t ^ Table[(b >> 4) & 15]));
        }

        return crc;
    }
}
