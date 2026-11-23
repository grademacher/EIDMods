using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace EIDMods.MapCore;

/// <summary>What a cache file was baked from; a mismatch means "rebake".</summary>
public sealed record CacheHeader(int FormatVersion, string MapKey, float CellSize, IReadOnlyCollection<ulong> ColliderSignatures);

/// <summary>
/// Gzip'd binary heightfield cache. Layout (inside the gzip stream): "EIDMAP", format version,
/// map key, cell size, originY, quantum, collider signatures, column count, then per column
/// (cx, cz, span count, spans), then "END". After the gzip data comes an uncompressed trailer
/// (compressed length + "EIDEND"), because GZipStream doesn't validate its own footer. Reading never
/// throws: anything unexpected (wrong map, format or cell size, truncation, corruption) returns
/// false so the caller rebakes.
/// </summary>
public static class HeightfieldCache
{
    public const int CurrentFormat = 1;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("EIDMAP");
    private static readonly byte[] EndMark = Encoding.ASCII.GetBytes("END");

    public static void Write(Stream s, Heightfield hf, CacheHeader header)
    {
        // .NET's GZipStream doesn't validate the gzip footer, so a truncated file can decompress
        // "fine". The payload is followed by its own uncompressed trailer: compressed length + "EIDEND".
        var compressed = new MemoryStream();
        using (var gz = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            WritePayload(gz, hf, header);

        s.Write(compressed.GetBuffer(), 0, (int)compressed.Length);
        var trailer = new BinaryWriter(s, Encoding.ASCII, leaveOpen: true);
        trailer.Write(compressed.Length);
        trailer.Write(FileEnd);
        trailer.Flush();
    }

    private static readonly byte[] FileEnd = Encoding.ASCII.GetBytes("EIDEND");
    private const int TrailerLength = sizeof(long) + 6;

    private static void WritePayload(Stream gz, Heightfield hf, CacheHeader header)
    {
        using var w = new BinaryWriter(gz, Encoding.UTF8, leaveOpen: true);

        w.Write(Magic);
        w.Write(header.FormatVersion);
        w.Write(header.MapKey ?? "");
        w.Write(header.CellSize);
        w.Write(hf.OriginY);
        w.Write(hf.Quantum);
        w.Write(header.ColliderSignatures.Count);
        foreach (var sig in header.ColliderSignatures) w.Write(sig);

        w.Write(hf.ColumnCount);
        foreach (var (cx, cz) in hf.Columns())
        {
            var spans = hf.GetSpans(cx, cz);
            w.Write(cx);
            w.Write(cz);
            w.Write(spans.Length);
            foreach (var sp in spans)
            {
                w.Write(sp.Bottom);
                w.Write(sp.Top);
                w.Write((byte)sp.Flags);
            }
        }
        w.Write(EndMark);
    }

    public static bool TryRead(Stream s, string expectedMapKey, float expectedCellSize, out Heightfield hf, out CacheHeader header)
    {
        hf = null;
        header = null;
        try
        {
            var all = new MemoryStream();
            s.CopyTo(all);
            var bytes = all.GetBuffer();
            long length = all.Length;
            if (length < TrailerLength) return false;
            for (int i = 0; i < FileEnd.Length; i++)
                if (bytes[length - FileEnd.Length + i] != FileEnd[i]) return false;
            long payload = BitConverter.ToInt64(bytes, (int)(length - TrailerLength));
            if (payload != length - TrailerLength) return false;

            using var gz = new GZipStream(new MemoryStream(bytes, 0, (int)payload), CompressionMode.Decompress);
            using var r = new BinaryReader(gz, Encoding.UTF8, leaveOpen: true);

            if (!Expect(r, Magic)) return false;
            int version = r.ReadInt32();
            if (version != CurrentFormat) return false;
            string key = r.ReadString();
            if (key != expectedMapKey) return false;
            float cellSize = r.ReadSingle();
            if (Math.Abs(cellSize - expectedCellSize) > 1e-6f) return false;
            float originY = r.ReadSingle(), quantum = r.ReadSingle();

            int sigCount = r.ReadInt32();
            if (sigCount < 0) return false;
            var sigs = new HashSet<ulong>();
            for (int i = 0; i < sigCount; i++) sigs.Add(r.ReadUInt64());

            var builder = new HeightfieldBuilder(cellSize, originY, quantum);
            int columns = r.ReadInt32();
            if (columns < 0) return false;
            for (int c = 0; c < columns; c++)
            {
                int cx = r.ReadInt32(), cz = r.ReadInt32(), n = r.ReadInt32();
                if (n < 0) return false;
                for (int i = 0; i < n; i++)
                    builder.AddSpan(cx, cz, r.ReadSingle(), r.ReadSingle(), (SpanFlags)r.ReadByte());
            }
            if (!Expect(r, EndMark)) return false;
            // Read to the end so the gzip footer is reached; a damaged/missing footer throws here.
            if (gz.ReadByte() != -1) return false;

            hf = builder.Build();
            header = new CacheHeader(version, key, cellSize, sigs);
            return true;
        }
        catch (Exception)
        {
            // Truncated/corrupt gzip, short reads, bad strings: treat as a cache miss.
            hf = null;
            header = null;
            return false;
        }
    }

    private static bool Expect(BinaryReader r, byte[] mark)
    {
        var got = r.ReadBytes(mark.Length);
        if (got.Length != mark.Length) return false;
        for (int i = 0; i < mark.Length; i++) if (got[i] != mark[i]) return false;
        return true;
    }
}
