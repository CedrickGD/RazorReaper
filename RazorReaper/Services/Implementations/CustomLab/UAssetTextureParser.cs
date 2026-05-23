using System.Buffers.Binary;
using RazorReaper.Models;

namespace RazorReaper.Services.Implementations.CustomLab;

/// <summary>
/// Pure byte-level parser for ARK's .uasset texture files. Locates the format marker
/// (PF_DXT5 / PF_B8G8R8A8) and walks back to extract width, height, data offset, and size.
///
/// Ported from t1m's Python inject_custom_sky.py — same heuristics, same dimension whitelist.
/// All functions operate on ReadOnlySpan&lt;byte&gt; with no I/O so they're cheap to unit-test.
/// </summary>
public static class UAssetTextureParser
{
    private static readonly byte[] Dxt5Marker = "PF_DXT5"u8.ToArray();
    private static readonly byte[] Bgra8Marker = "PF_B8G8R8A8\0"u8.ToArray();

    private static readonly HashSet<int> ValidDimensions = new()
    {
        64, 128, 256, 512, 1024, 2048, 4096, 8192
    };

    /// <summary>
    /// Parse a DXT5 (BC3) sky texture.
    ///
    /// DXT5 data is stored at the end of the file: data starts at <c>file.Length - w*h - 4</c>
    /// (4-byte trailer after the compressed block).
    /// </summary>
    public static bool TryParseDxt5(ReadOnlySpan<byte> raw, out int width, out int height, out int dataOffset, out int dataSize)
    {
        width = 0;
        height = 0;
        dataOffset = 0;
        dataSize = 0;

        var markerOffset = LastIndexOf(raw, Dxt5Marker);
        if (markerOffset < 0) return false;

        // Walk back up to 120 bytes from the marker, scanning 4-byte aligned positions for two
        // consecutive int32s that are both in the valid dimension set.
        var lookbackStart = Math.Max(0, markerOffset - 120);
        var chunk = raw.Slice(lookbackStart, markerOffset - lookbackStart);

        // Match the Python: step backward from chunk.Length - 8, stepping 4.
        for (var j = chunk.Length - 8; j >= 0; j -= 4)
        {
            var w = BinaryPrimitives.ReadInt32LittleEndian(chunk.Slice(j, 4));
            var h = BinaryPrimitives.ReadInt32LittleEndian(chunk.Slice(j + 4, 4));
            if (ValidDimensions.Contains(w) && ValidDimensions.Contains(h))
            {
                width = w;
                height = h;
                // DXT5/BC3 compressed size: 4×4 blocks → 16 bytes per 16 pixels = w*h bytes.
                dataSize = w * h;
                dataOffset = raw.Length - dataSize - 4;
                if (dataOffset < 100) return false;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parse a BGRA8 (uncompressed) texture used by some mods.
    ///
    /// Format marker is followed by 32 bytes of metadata: int32 element_count at +16,
    /// int64 data_offset at +24. Width/height are 16/12 bytes BEFORE the marker.
    /// If data_offset == 0, we walk the mipmap chain to compute the offset from the file end.
    /// </summary>
    public static bool TryParseBgra8(ReadOnlySpan<byte> raw, out int width, out int height, out int dataOffset, out int dataSize)
    {
        width = 0;
        height = 0;
        dataOffset = 0;
        dataSize = 0;

        var markerOffset = LastIndexOf(raw, Bgra8Marker);
        if (markerOffset < 0 || markerOffset < 0x200) return false;

        var markerEnd = markerOffset + Bgra8Marker.Length;
        if (markerEnd + 32 > raw.Length) return false;

        var elementCount = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(markerEnd + 16, 4));
        var off = (int)BinaryPrimitives.ReadInt64LittleEndian(raw.Slice(markerEnd + 24, 8));
        dataSize = elementCount;

        // Width/height are 16/12 bytes before the marker.
        if (markerOffset - 16 < 0) return false;
        var w = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(markerOffset - 16, 4));
        var h = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(markerOffset - 12, 4));

        // Validate. Fallback: if w*h*4 != data_size but data_size has a clean square root, assume square texture.
        if (!(w > 0 && h > 0 && (long)w * h * 4 == dataSize))
        {
            var side = (int)Math.Round(Math.Sqrt(dataSize / 4.0));
            if (side > 0 && (long)side * side * 4 == dataSize)
            {
                w = side;
                h = side;
            }
            else
            {
                return false;
            }
        }

        if (dataSize <= 0) return false;

        // data_offset == 0 means the texture has mipmaps; the largest mip starts at the file end
        // minus the sum of all mip sizes.
        if (off == 0)
        {
            long total = 0;
            int mw = w, mh = h;
            while (true)
            {
                total += (long)mw * mh * 4;
                if (mw == 1 && mh == 1) break;
                mw = Math.Max(1, mw / 2);
                mh = Math.Max(1, mh / 2);
            }
            if (total >= raw.Length || total > int.MaxValue) return false;
            off = raw.Length - (int)total;
        }

        if (off <= 0 || off >= raw.Length) return false;
        if (off + dataSize > raw.Length) return false;

        width = w;
        height = h;
        dataOffset = off;
        return true;
    }

    /// <summary>
    /// Last-occurrence search for a byte pattern. Mirrors Python's bytes.rfind / repeated bytes.find.
    /// </summary>
    private static int LastIndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length) return -1;

        // Search forward, keep the last match. This matches the Python loop semantics
        // (repeated find from increasing offset).
        var last = -1;
        var pos = 0;
        while (pos <= haystack.Length - needle.Length)
        {
            var idx = haystack.Slice(pos).IndexOf(needle);
            if (idx < 0) break;
            last = pos + idx;
            pos = last + 1;
        }
        return last;
    }
}
