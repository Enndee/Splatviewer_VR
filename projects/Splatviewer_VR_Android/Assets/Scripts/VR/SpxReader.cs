// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using GaussianSplatting.Runtime;
using Unity.Mathematics;
using WebP;
using static RuntimeSplatLoader;

/// <summary>
/// Reads .spx (Reall3D SPX v2/v3) Gaussian Splat files.
/// Supports open formats 22, 23, 220, 230 (basic splat data) and SH palette formats 8, 9.
/// </summary>
internal static class SpxReader
{
    const int HeaderSize = 128;

    public static SplatData[] ReadFile(string path)
    {
        byte[] fileData = File.ReadAllBytes(path);
        if (fileData.Length < HeaderSize)
            throw new Exception("SPX file too small for header");

        // Validate magic "spx"
        if (fileData[0] != (byte)'s' || fileData[1] != (byte)'p' || fileData[2] != (byte)'x')
            throw new Exception("Invalid SPX magic");

        int shDegree = fileData[52];
        if (shDegree < 0 || shDegree > 3) shDegree = 0;

        // Parse data blocks — accumulate splats from multiple blocks
        var splatChunks = new List<SplatData[]>();
        float3[][] shPalette = null;

        int offset = HeaderSize;
        while (offset + 4 <= fileData.Length)
        {
            uint blockDef = BitConverter.ToUInt32(fileData, offset);
            offset += 4;

            bool compressed = ((blockDef >> 31) & 1) != 0;
            int compressionType = (int)((blockDef >> 28) & 0x7);
            int blockLength = (int)(blockDef & 0x0FFFFFFF);

            if (blockLength <= 0 || offset + blockLength > fileData.Length)
                break;

            byte[] blockData;
            if (compressed)
            {
                if (compressionType == 0) // gzip
                    blockData = DecompressGzip(fileData, offset, blockLength);
                else
                {
                    offset += blockLength;
                    continue; // unsupported compression (xz etc.), skip
                }
            }
            else
            {
                blockData = new byte[blockLength];
                Buffer.BlockCopy(fileData, offset, blockData, 0, blockLength);
            }
            offset += blockLength;

            if (blockData.Length < 8) continue;

            uint formatId = BitConverter.ToUInt32(blockData, 4);

            switch (formatId)
            {
                case 22:
                    splatChunks.Add(DecodeFormat22(blockData, shDegree, logTimes: 1));
                    break;
                case 23:
                    splatChunks.Add(DecodeFormat22(blockData, shDegree, logTimes: 0));
                    break;
                case 220:
                    splatChunks.Add(DecodeFormat220(blockData, shDegree, logTimes: 1));
                    break;
                case 230:
                    splatChunks.Add(DecodeFormat220(blockData, shDegree, logTimes: 0));
                    break;
                case 8:
                    shPalette = DecodeSHPalette(blockData, shDegree);
                    break;
                case 9:
                    shPalette = DecodeSHPaletteWebP(blockData, shDegree);
                    break;
            }
        }

        if (splatChunks.Count == 0)
            throw new Exception("No supported splat data block found in SPX file");

        // Merge all chunks into a single array
        int totalCount = 0;
        foreach (var chunk in splatChunks)
            totalCount += chunk.Length;

        SplatData[] splats;
        if (splatChunks.Count == 1)
        {
            splats = splatChunks[0];
        }
        else
        {
            splats = new SplatData[totalCount];
            int destIdx = 0;
            foreach (var chunk in splatChunks)
            {
                Array.Copy(chunk, 0, splats, destIdx, chunk.Length);
                destIdx += chunk.Length;
            }
        }

        // Apply SH palette if available
        if (shPalette != null)
            ApplySHPalette(splats, shPalette);

        return splats;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Flip rows in-place to undo the bottom-to-top row order
    /// produced by LoadRGBAFromWebP (which uses negative stride for Unity textures).
    /// </summary>
    static void FlipRows(byte[] data, int w, int h)
    {
        int stride = w * 4;
        byte[] tmp = new byte[stride];
        for (int y = 0; y < h / 2; y++)
        {
            int top = y * stride;
            int bot = (h - 1 - y) * stride;
            Buffer.BlockCopy(data, top, tmp, 0, stride);
            Buffer.BlockCopy(data, bot, data, top, stride);
            Buffer.BlockCopy(tmp, 0, data, bot, stride);
        }
    }

    static byte[] DecompressGzip(byte[] data, int offset, int length)
    {
        using var ms = new MemoryStream(data, offset, length);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gz.CopyTo(output);
        return output.ToArray();
    }

    // ── Position log-decoding ─────────────────────────────────────────────────

    static float DecodeLog(float v, int times)
    {
        for (int i = 0; i < times; i++)
            v = v < 0 ? -(math.exp(-v) - 1f) : math.exp(v) - 1f;
        return v;
    }

    static int SignExtend24(int v)
    {
        return (v & 0x800000) != 0 ? v | unchecked((int)0xFF000000) : v;
    }

    // ── Format 22/23: Structure-of-arrays, raw bytes ──────────────────────────
    //
    // Layout (offset = 8 after count+formatId):
    //   n bytes × 9 channels: x0,y0,z0, x1,y1,z1, x2,y2,z2  (position bytes)
    //   n bytes × 3 channels: sx,sy,sz                        (scale)
    //   n bytes × 4 channels: R,G,B,A                         (color)
    //   n bytes × 4 channels: rw,rx,ry,rz                     (rotation)
    //   n bytes × 2 channels: p0,p1                            (palette, if SH > 0)

    static SplatData[] DecodeFormat22(byte[] data, int shDegree, int logTimes)
    {
        int n = (int)BitConverter.ToUInt32(data, 0);
        if (n <= 0) throw new Exception("SPX format 22: invalid gaussian count");
        int off = 8;

        // Validate minimum data size: 20 bytes/gaussian (+ 2 palette bytes if SH > 0)
        int bytesPerGaussian = shDegree > 0 ? 22 : 20;
        if (data.Length < off + n * bytesPerGaussian)
            throw new Exception($"SPX format 22: data truncated ({data.Length} < {off + n * bytesPerGaussian})");

        var splats = new SplatData[n];

        for (int i = 0; i < n; i++)
        {
            ref SplatData s = ref splats[i];

            // Position: 24-bit signed, 3 interleaved byte planes per axis
            int i32x = SignExtend24(data[off + i] | (data[off + n * 3 + i] << 8) | (data[off + n * 6 + i] << 16));
            int i32y = SignExtend24(data[off + n + i] | (data[off + n * 4 + i] << 8) | (data[off + n * 7 + i] << 16));
            int i32z = SignExtend24(data[off + n * 2 + i] | (data[off + n * 5 + i] << 8) | (data[off + n * 8 + i] << 16));

            float fx = i32x / 4096f;
            float fy = i32y / 4096f;
            float fz = i32z / 4096f;

            if (logTimes > 0)
            {
                fx = DecodeLog(fx, logTimes);
                fy = DecodeLog(fy, logTimes);
                fz = DecodeLog(fz, logTimes);
            }
            s.pos = new float3(fx, fy, fz);

            // Scale: exp(byte/16 - 10)
            s.scale = new float3(
                math.exp(data[off + n * 9 + i] / 16f - 10f),
                math.exp(data[off + n * 10 + i] / 16f - 10f),
                math.exp(data[off + n * 11 + i] / 16f - 10f)
            );

            // Color: direct RGBA [0,255] → [0,1]
            s.dc0 = new float3(
                data[off + n * 12 + i] / 255f,
                data[off + n * 13 + i] / 255f,
                data[off + n * 14 + i] / 255f
            );
            s.opacity = data[off + n * 15 + i] / 255f;

            // Rotation: (w,x,y,z) from 4 bytes, each (b-128)/128
            float rw = (data[off + n * 16 + i] - 128f) / 128f;
            float rx = (data[off + n * 17 + i] - 128f) / 128f;
            float ry = (data[off + n * 18 + i] - 128f) / 128f;
            float rz = (data[off + n * 19 + i] - 128f) / 128f;
            float4 q = math.normalize(new float4(rx, ry, rz, rw)); // (x,y,z,w)
            s.rot = GaussianUtils.PackSmallest3Rotation(q);

            // Store palette index in sh[0].x for later SH palette lookup
            if (shDegree > 0)
            {
                int paletteIdx = data[off + n * 20 + i] | (data[off + n * 21 + i] << 8);
                s.sh = new float3[15]; // placeholder, filled during palette application
                s.sh[0] = new float3(paletteIdx, 0, 0); // temporarily store index
            }
        }

        return splats;
    }

    // ── Format 220/230: WebP-encoded sections ─────────────────────────────────
    //
    // Layout after count(4)+formatId(4):
    //   [length(4), webp(x0,y0,z0,255)] [length(4), webp(x1,y1,z1,255)]
    //   [length(4), webp(x2,y2,z2,255)] [length(4), webp(sx,sy,sz,255)]
    //   [length(4), webp(r,g,b,a)]      [length(4), webp(rx,ry,rz,ri)]
    //   [length(4), webp(p0,p1,0,255)]  (optional, if SH > 0)

    static SplatData[] DecodeFormat220(byte[] data, int shDegree, int logTimes)
    {
        int n = (int)BitConverter.ToUInt32(data, 0);
        if (n <= 0) throw new Exception("SPX format 220: invalid gaussian count");

        // Decode WebP sections
        int sectionCount = shDegree > 0 ? 7 : 6;
        byte[][] sections = new byte[sectionCount][];
        int off = 8;
        for (int sec = 0; sec < sectionCount; sec++)
        {
            if (off + 4 > data.Length)
                throw new Exception($"SPX format 220: data truncated reading section {sec} length");
            int len = (int)BitConverter.ToUInt32(data, off);
            off += 4;
            if (off + len > data.Length)
                throw new Exception($"SPX format 220: data truncated in section {sec}");

            byte[] webpBytes = new byte[len];
            Buffer.BlockCopy(data, off, webpBytes, 0, len);
            off += len;

            int w = 0, h = 0;
            Texture2DExt.GetWebPDimensions(webpBytes, out w, out h);
            Error error;
            byte[] rgba = Texture2DExt.LoadRGBAFromWebP(webpBytes, ref w, ref h, false, out error);
            if (error != Error.Success || rgba == null)
                throw new Exception($"SPX format 220: failed to decode WebP section {sec}: {error}");
            FlipRows(rgba, w, h); // Undo bottom-to-top row order from WebP decoder
            sections[sec] = rgba;
        }

        var splats = new SplatData[n];
        byte[] posLow = sections[0], posMid = sections[1], posHigh = sections[2];
        byte[] scaleData = sections[3], colorData = sections[4], rotData = sections[5];
        byte[] paletteData = sectionCount > 6 ? sections[6] : null;

        const float SQRT2 = 1.4142135623730951f;

        for (int i = 0; i < n; i++)
        {
            ref SplatData s = ref splats[i];
            int p = i * 4; // 4 bytes per pixel (RGBA)

            // Position: 24-bit signed from 3 WebP images
            int i32x = SignExtend24(posLow[p] | (posMid[p] << 8) | (posHigh[p] << 16));
            int i32y = SignExtend24(posLow[p + 1] | (posMid[p + 1] << 8) | (posHigh[p + 1] << 16));
            int i32z = SignExtend24(posLow[p + 2] | (posMid[p + 2] << 8) | (posHigh[p + 2] << 16));

            float fx = i32x / 4096f;
            float fy = i32y / 4096f;
            float fz = i32z / 4096f;

            if (logTimes > 0)
            {
                fx = DecodeLog(fx, logTimes);
                fy = DecodeLog(fy, logTimes);
                fz = DecodeLog(fz, logTimes);
            }
            s.pos = new float3(fx, fy, fz);

            // Scale: exp(byte/16 - 10)
            s.scale = new float3(
                math.exp(scaleData[p] / 16f - 10f),
                math.exp(scaleData[p + 1] / 16f - 10f),
                math.exp(scaleData[p + 2] / 16f - 10f)
            );

            // Color: direct RGBA [0,255]
            s.dc0 = new float3(colorData[p] / 255f, colorData[p + 1] / 255f, colorData[p + 2] / 255f);
            s.opacity = colorData[p + 3] / 255f;

            // Rotation: smallest-3 encoding with index
            float rx = (rotData[p] / 255f - 0.5f) * SQRT2;
            float ry = (rotData[p + 1] / 255f - 0.5f) * SQRT2;
            float rz = (rotData[p + 2] / 255f - 0.5f) * SQRT2;
            int ri = rotData[p + 3] - 252;

            float rMissing = 1f - (rx * rx + ry * ry + rz * rz);
            rMissing = rMissing < 0f ? 0f : math.sqrt(rMissing);

            float4 q;
            switch (ri)
            {
                // C++ reconstructs (w,x,y,z); we need (x,y,z,w) for PackSmallest3Rotation
                case 0: q = new float4(rx, ry, rz, rMissing); break;           // w is largest → (x,y,z,w)=(rx,ry,rz,RI)
                case 1: q = new float4(rMissing, ry, rz, rx); break;           // x is largest → (x,y,z,w)=(RI,ry,rz,rx)
                case 2: q = new float4(ry, rMissing, rz, rx); break;           // y is largest → (x,y,z,w)=(ry,RI,rz,rx)
                default: q = new float4(ry, rz, rMissing, rx); break;          // z is largest → (x,y,z,w)=(ry,rz,RI,rx)
            }
            // q is now (x,y,z,w)
            q = math.normalize(q);
            s.rot = GaussianUtils.PackSmallest3Rotation(q);

            // Palette index for SH
            if (paletteData != null)
            {
                int paletteIdx = paletteData[p] | (paletteData[p + 1] << 8);
                s.sh = new float3[15];
                s.sh[0] = new float3(paletteIdx, 0, 0);
            }
        }

        return splats;
    }

    // ── SH Palette Format 8: raw bytes (r,g,b,255) × 15 per palette entry ────

    static float3[][] DecodeSHPalette(byte[] data, int shDegree)
    {
        if (data.Length < 8) return null;
        // Format 8 block: reserved(4) + formatId(4) + data
        // Data: [sh0,sh1,...sh14, sh0,sh1,...sh14, ...] as (r,g,b,255) pixels
        int off = 8;
        int remaining = data.Length - off;
        int bytesPerEntry = 15 * 4; // 15 SH coefficients × 4 bytes (RGBA)
        int entryCount = remaining / bytesPerEntry;
        if (entryCount <= 0) return null;

        var palette = new float3[entryCount][];
        for (int e = 0; e < entryCount; e++)
        {
            palette[e] = new float3[15];
            for (int j = 0; j < 15; j++)
            {
                int p = off + e * bytesPerEntry + j * 4;
                palette[e][j] = new float3(
                    (data[p] - 128f) / 128f,
                    (data[p + 1] - 128f) / 128f,
                    (data[p + 2] - 128f) / 128f
                );
            }
        }
        return palette;
    }

    // ── SH Palette Format 9: WebP-encoded palette ────────────────────────────

    static float3[][] DecodeSHPaletteWebP(byte[] data, int shDegree)
    {
        if (data.Length < 8) return null;
        // Format 9: reserved(4) + formatId(4) + webp data
        int off = 8;
        int webpLen = data.Length - off;
        byte[] webpBytes = new byte[webpLen];
        Buffer.BlockCopy(data, off, webpBytes, 0, webpLen);

        int w = 0, h = 0;
        Texture2DExt.GetWebPDimensions(webpBytes, out w, out h);
        Error error;
        byte[] rgba = Texture2DExt.LoadRGBAFromWebP(webpBytes, ref w, ref h, false, out error);
        if (error != Error.Success || rgba == null) return null;
        FlipRows(rgba, w, h);

        int totalPixels = w * h;
        int entryCount = totalPixels / 15;
        if (entryCount <= 0) return null;

        var palette = new float3[entryCount][];
        for (int e = 0; e < entryCount; e++)
        {
            palette[e] = new float3[15];
            for (int j = 0; j < 15; j++)
            {
                int p = (e * 15 + j) * 4;
                palette[e][j] = new float3(
                    (rgba[p] - 128f) / 128f,
                    (rgba[p + 1] - 128f) / 128f,
                    (rgba[p + 2] - 128f) / 128f
                );
            }
        }
        return palette;
    }

    // ── Apply SH palette to splats ───────────────────────────────────────────

    static void ApplySHPalette(SplatData[] splats, float3[][] palette)
    {
        for (int i = 0; i < splats.Length; i++)
        {
            if (splats[i].sh == null) continue;
            int idx = (int)splats[i].sh[0].x;
            if (idx >= 0 && idx < palette.Length)
            {
                for (int j = 0; j < 15 && j < palette[idx].Length; j++)
                    splats[i].sh[j] = palette[idx][j];
            }
            else
            {
                splats[i].sh = null; // invalid palette index
            }
        }
    }
}
