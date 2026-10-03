using System;
using System.Collections.Generic;
using System.Text;

namespace Radio808.Shared.Map;

/// <summary>A Mapbox Vector Tile decoded just far enough to draw: layers of features with tags and geometry.</summary>
public sealed class MvtTile
{
    public readonly List<MvtLayer> Layers = new();

    public MvtLayer? Layer(string name) => Layers.Find(l => l.Name == name);

    public static MvtTile Decode(byte[] data)
    {
        var tile = new MvtTile();
        var r = new Pb(data, 0, data.Length);
        while (r.Next(out int field, out int wire))
        {
            if (field == 3 && wire == 2) tile.Layers.Add(MvtLayer.Decode(r.Sub()));
            else r.Skip(wire);
        }
        return tile;
    }
}

public enum MvtGeom { Unknown = 0, Point = 1, Line = 2, Polygon = 3 }

public sealed class MvtLayer
{
    public string Name = "";
    public int Extent = 4096;
    public readonly List<string> Keys = new();
    public readonly List<object> Values = new();
    public readonly List<MvtFeature> Features = new();

    public static MvtLayer Decode(Pb r)
    {
        var l = new MvtLayer();
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case 1: l.Name = r.String(); break;
                case 2: l.Features.Add(MvtFeature.Decode(r.Sub())); break;
                case 3: l.Keys.Add(r.String()); break;
                case 4: l.Values.Add(DecodeValue(r.Sub())); break;
                case 5: l.Extent = (int)r.Varint(); break;
                default: r.Skip(wire); break;
            }
        }
        // the keys and values tables usually follow the features in the stream, so tags resolve afterwards
        foreach (var f in l.Features) f.ResolveTags(l);
        return l;
    }

    private static object DecodeValue(Pb r)
    {
        object v = "";
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case 1: v = r.String(); break;
                case 2: v = (double)r.Float(); break;
                case 3: v = r.Double(); break;
                case 4: v = (double)(long)r.Varint(); break;
                case 5: v = (double)r.Varint(); break;
                case 6: v = (double)Pb.Zigzag(r.Varint()); break;
                case 7: v = r.Varint() != 0; break;
                default: r.Skip(wire); break;
            }
        }
        return v;
    }
}

public sealed class MvtFeature
{
    public MvtGeom Type;
    public readonly Dictionary<string, object> Tags = new();
    /// <summary>Rings / lines / points in tile units (0..extent), as decoded from the command stream.</summary>
    public readonly List<MapPt[]> Parts = new();

    public string? Str(string key) => Tags.TryGetValue(key, out var v) ? v as string : null;
    public double Num(string key, double dflt = 0) => Tags.TryGetValue(key, out var v) && v is double d ? d : dflt;

    private List<(int k, int v)>? _tagIndexes;

    internal void ResolveTags(MvtLayer layer)
    {
        if (_tagIndexes == null) return;
        foreach (var (k, v) in _tagIndexes)
            if (k < layer.Keys.Count && v < layer.Values.Count) Tags[layer.Keys[k]] = layer.Values[v];
        _tagIndexes = null;
    }

    public static MvtFeature Decode(Pb r)
    {
        var f = new MvtFeature();
        List<uint>? geometry = null;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case 2:
                    {
                        var t = r.Sub();
                        f._tagIndexes = new List<(int, int)>();
                        while (!t.End) f._tagIndexes.Add(((int)t.Varint(), (int)t.Varint()));
                        break;
                    }
                case 3: f.Type = (MvtGeom)r.Varint(); break;
                case 4:
                    {
                        var g = r.Sub();
                        geometry = new List<uint>();
                        while (!g.End) geometry.Add((uint)g.Varint());
                        break;
                    }
                default: r.Skip(wire); break;
            }
        }
        if (geometry != null) f.DecodeGeometry(geometry);
        return f;
    }

    private void DecodeGeometry(List<uint> g)
    {
        long x = 0, y = 0;
        var cur = new List<MapPt>();
        int i = 0;
        while (i < g.Count)
        {
            uint cmd = g[i++];
            int id = (int)(cmd & 7), count = (int)(cmd >> 3);
            if (id == 1)   // MoveTo
            {
                for (int k = 0; k < count; k++)
                {
                    if (cur.Count > 0) { Parts.Add(cur.ToArray()); cur.Clear(); }
                    x += Pb.Zigzag(g[i++]); y += Pb.Zigzag(g[i++]);
                    cur.Add(new MapPt(x, y));
                }
            }
            else if (id == 2)   // LineTo
            {
                for (int k = 0; k < count; k++)
                {
                    x += Pb.Zigzag(g[i++]); y += Pb.Zigzag(g[i++]);
                    cur.Add(new MapPt(x, y));
                }
            }
            else if (id == 7)   // ClosePath
            {
                if (cur.Count > 0) { Parts.Add(cur.ToArray()); cur.Clear(); }
            }
            else break;
        }
        if (cur.Count > 0) Parts.Add(cur.ToArray());
    }
}

/// <summary>A minimal protobuf wire-format reader.</summary>
public sealed class Pb
{
    private readonly byte[] _d;
    private int _pos;
    private readonly int _end;

    public Pb(byte[] d, int start, int end) { _d = d; _pos = start; _end = end; }
    public bool End => _pos >= _end;

    public bool Next(out int field, out int wire)
    {
        if (_pos >= _end) { field = wire = 0; return false; }
        ulong key = Varint();
        field = (int)(key >> 3); wire = (int)(key & 7);
        return true;
    }

    public ulong Varint()
    {
        ulong v = 0; int shift = 0;
        while (_pos < _end)
        {
            byte b = _d[_pos++];
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return v;
    }

    public static long Zigzag(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);
    public static long Zigzag(uint v) => (long)(v >> 1) ^ -(long)(v & 1);

    public Pb Sub()
    {
        int len = (int)Varint();
        var s = new Pb(_d, _pos, _pos + len);
        _pos += len;
        return s;
    }

    public string String()
    {
        int len = (int)Varint();
        var s = Encoding.UTF8.GetString(_d, _pos, len);
        _pos += len;
        return s;
    }

    public float Float() { var v = BitConverter.ToSingle(_d, _pos); _pos += 4; return v; }
    public double Double() { var v = BitConverter.ToDouble(_d, _pos); _pos += 8; return v; }

    public void Skip(int wire)
    {
        switch (wire)
        {
            case 0: Varint(); break;
            case 1: _pos += 8; break;
            case 2: _pos += (int)Varint(); break;
            case 5: _pos += 4; break;
            default: _pos = _end; break;
        }
    }
}
