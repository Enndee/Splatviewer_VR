// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using GaussianSplatting.Runtime;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Loads .ply and .spz Gaussian Splat files at runtime, creating a GaussianSplatAsset
/// in memory and assigning it to a GaussianSplatRenderer.
/// Uses Float32 quality (no chunking) for maximum simplicity.
/// </summary>
public class RuntimeSplatLoader : MonoBehaviour
{
    [Tooltip("The GaussianSplatRenderer to load splats into.")]
    public GaussianSplatRenderer targetRenderer;

    GaussianSplatAsset _currentAsset;

    void Awake()
    {
        if (targetRenderer == null)
            targetRenderer = GetComponent<GaussianSplatRenderer>();
    }

    /// <summary>Load a .ply or .spz file from disk and display it.</summary>
    public bool LoadFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Debug.LogError($"[RuntimeSplatLoader] File not found: {filePath}");
            return false;
        }

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext != ".ply" && ext != ".spz")
        {
            Debug.LogError($"[RuntimeSplatLoader] Unsupported format: {ext}");
            return false;
        }

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var splats = ext == ".spz" ? ReadSpz(filePath) : ReadPly(filePath);
            if (splats == null || splats.Length == 0)
                return false;

            Debug.Log($"[RuntimeSplatLoader] Read {splats.Length:N0} splats in {sw.ElapsedMilliseconds}ms");

            // Compute bounds
            float3 bMin = float.PositiveInfinity;
            float3 bMax = float.NegativeInfinity;
            for (int i = 0; i < splats.Length; i++)
            {
                bMin = math.min(bMin, splats[i].pos);
                bMax = math.max(bMax, splats[i].pos);
            }

            // Morton reorder for better GPU cache coherence
            MortonReorder(splats, bMin, bMax);

            // Pack data into binary buffers
            byte[] posData = PackPositions(splats);
            byte[] otherData = PackOther(splats);
            byte[] colorData = PackColor(splats);
            byte[] shData = PackSH(splats);

            // Create asset
            if (_currentAsset != null)
                Destroy(_currentAsset);

            _currentAsset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
            _currentAsset.Initialize(
                splats.Length,
                GaussianSplatAsset.VectorFormat.Float32,
                GaussianSplatAsset.VectorFormat.Float32,
                GaussianSplatAsset.ColorFormat.Float32x4,
                GaussianSplatAsset.SHFormat.Float32,
                (Vector3)bMin, (Vector3)bMax, null
            );
            _currentAsset.name = Path.GetFileNameWithoutExtension(filePath);
            _currentAsset.runtimePosData = posData;
            _currentAsset.runtimeOtherData = otherData;
            _currentAsset.runtimeColorData = colorData;
            _currentAsset.runtimeSHData = shData;
            _currentAsset.SetDataHash(new Hash128((uint)splats.Length, (uint)GaussianSplatAsset.kCurrentVersion, 0, (uint)filePath.GetHashCode()));

            // Assign to renderer — Update() auto-detects the change
            targetRenderer.m_Asset = _currentAsset;
            Debug.Log($"[RuntimeSplatLoader] Loaded \"{_currentAsset.name}\" ({splats.Length:N0} splats) in {sw.ElapsedMilliseconds}ms");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RuntimeSplatLoader] Failed to load {filePath}: {ex}");
            return false;
        }
    }

    void OnDestroy()
    {
        if (_currentAsset != null)
            Destroy(_currentAsset);
    }

    // ── PLY Reader ────────────────────────────────────────────────────────────

    struct SplatData
    {
        public float3 pos;
        public float3 dc0;     // color after SH0ToColor (ready for texture)
        public float opacity;  // [0,1] after Sigmoid (ready for texture)
        public float3 scale;   // linearized scale (after exp)
        public float4 rot;     // packed smallest-3 rotation from PackSmallest3Rotation
        public float3[] sh;    // SH bands 1-15, interleaved (R,G,B) per band (may be null)
    }

    static SplatData[] ReadPly(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var reader = new BinaryReader(fs, Encoding.ASCII);

        // Parse header
        var props = new List<string>();
        int vertexCount = 0;
        bool inVertex = false;
        string headerEnd;
        long headerBytes = 0;

        using (var headerReader = new StreamReader(fs, Encoding.ASCII, false, 4096, leaveOpen: true))
        {
            while (true)
            {
                string line = headerReader.ReadLine();
                if (line == null)
                    throw new Exception("Unexpected end of PLY header");

                headerBytes += Encoding.ASCII.GetByteCount(line) + 1; // +1 for newline

                if (line.StartsWith("element vertex"))
                {
                    vertexCount = int.Parse(line.Split(' ')[2]);
                    inVertex = true;
                }
                else if (line.StartsWith("element "))
                {
                    inVertex = false;
                }
                else if (line.StartsWith("property") && inVertex)
                {
                    // "property float x" → extract "x"
                    var parts = line.Split(' ');
                    if (parts.Length >= 3)
                        props.Add(parts[2]);
                }
                else if (line == "end_header")
                {
                    headerBytes += 0; // already counted
                    break;
                }
            }
        }

        // Seek to start of binary data (header parsing consumed some buffer)
        // Recompute: scan forward for "end_header\n"
        fs.Seek(0, SeekOrigin.Begin);
        byte[] allHeader = new byte[Math.Min(fs.Length, 64 * 1024)];
        fs.Read(allHeader, 0, allHeader.Length);
        string headerStr = Encoding.ASCII.GetString(allHeader);
        int endIdx = headerStr.IndexOf("end_header\n", StringComparison.Ordinal);
        if (endIdx < 0)
        {
            endIdx = headerStr.IndexOf("end_header\r\n", StringComparison.Ordinal);
            if (endIdx < 0)
                throw new Exception("Could not find end_header in PLY");
            endIdx += "end_header\r\n".Length;
        }
        else
        {
            endIdx += "end_header\n".Length;
        }
        fs.Seek(endIdx, SeekOrigin.Begin);

        if (vertexCount <= 0 || props.Count == 0)
            throw new Exception($"Invalid PLY: {vertexCount} vertices, {props.Count} properties");

        // Build property index map
        var propIdx = new Dictionary<string, int>();
        for (int i = 0; i < props.Count; i++)
            propIdx[props[i]] = i;

        int stride = props.Count; // number of floats per vertex
        bool hasSH = propIdx.ContainsKey("f_rest_0");
        int shCount = 0;
        if (hasSH)
        {
            for (int i = 0; i < 45; i++)
                if (propIdx.ContainsKey($"f_rest_{i}"))
                    shCount = i + 1;
        }

        // Read binary data
        var splats = new SplatData[vertexCount];
        var floatBuf = new float[stride];
        var byteBuf = new byte[stride * 4];

        for (int v = 0; v < vertexCount; v++)
        {
            int bytesRead = fs.Read(byteBuf, 0, byteBuf.Length);
            if (bytesRead < byteBuf.Length)
                throw new Exception($"PLY truncated at vertex {v}");
            Buffer.BlockCopy(byteBuf, 0, floatBuf, 0, byteBuf.Length);

            ref SplatData s = ref splats[v];

            s.pos = new float3(
                propIdx.ContainsKey("x") ? floatBuf[propIdx["x"]] : 0,
                propIdx.ContainsKey("y") ? floatBuf[propIdx["y"]] : 0,
                propIdx.ContainsKey("z") ? floatBuf[propIdx["z"]] : 0
            );

            // Read raw SH DC0 and apply SH0ToColor (linearize for texture)
            float3 rawDc0 = new float3(
                propIdx.ContainsKey("f_dc_0") ? floatBuf[propIdx["f_dc_0"]] : 0,
                propIdx.ContainsKey("f_dc_1") ? floatBuf[propIdx["f_dc_1"]] : 0,
                propIdx.ContainsKey("f_dc_2") ? floatBuf[propIdx["f_dc_2"]] : 0
            );
            s.dc0 = GaussianUtils.SH0ToColor(rawDc0);

            // Read raw logit opacity and apply Sigmoid
            float rawOpacity = propIdx.ContainsKey("opacity") ? floatBuf[propIdx["opacity"]] : 0;
            s.opacity = GaussianUtils.Sigmoid(rawOpacity);

            // Read raw log-scale and linearize (same as GaussianUtils.LinearScale)
            s.scale = GaussianUtils.LinearScale(new float3(
                propIdx.ContainsKey("scale_0") ? floatBuf[propIdx["scale_0"]] : 0,
                propIdx.ContainsKey("scale_1") ? floatBuf[propIdx["scale_1"]] : 0,
                propIdx.ContainsKey("scale_2") ? floatBuf[propIdx["scale_2"]] : 0
            ));

            // PLY stores as (w,x,y,z) in rot_0..rot_3
            float4 q = new float4(
                propIdx.ContainsKey("rot_0") ? floatBuf[propIdx["rot_0"]] : 1,
                propIdx.ContainsKey("rot_1") ? floatBuf[propIdx["rot_1"]] : 0,
                propIdx.ContainsKey("rot_2") ? floatBuf[propIdx["rot_2"]] : 0,
                propIdx.ContainsKey("rot_3") ? floatBuf[propIdx["rot_3"]] : 0
            );
            // Normalize and swizzle from (w,x,y,z) → (x,y,z,w), then pack smallest-3
            q = GaussianUtils.NormalizeSwizzleRotation(q);
            s.rot = GaussianUtils.PackSmallest3Rotation(q);

            // SH in PLY: f_rest_0..14 = R, f_rest_15..29 = G, f_rest_30..44 = B
            // We need interleaved: sh[j] = (R[j], G[j], B[j])
            if (shCount > 0)
            {
                s.sh = new float3[15];
                for (int j = 0; j < 15; j++)
                {
                    float sr = (j < shCount && propIdx.ContainsKey($"f_rest_{j}")) ? floatBuf[propIdx[$"f_rest_{j}"]] : 0;
                    float sg = (j + 15 < shCount && propIdx.ContainsKey($"f_rest_{j + 15}")) ? floatBuf[propIdx[$"f_rest_{j + 15}"]] : 0;
                    float sb = (j + 30 < shCount && propIdx.ContainsKey($"f_rest_{j + 30}")) ? floatBuf[propIdx[$"f_rest_{j + 30}"]] : 0;
                    s.sh[j] = new float3(sr, sg, sb);
                }
            }
        }

        return splats;
    }

    // ── SPZ Reader ────────────────────────────────────────────────────────────

    static SplatData[] ReadSpz(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        byte[] raw = ms.ToArray();

        if (raw.Length < 16)
            throw new Exception("SPZ file too small for header");

        // Parse header (16 bytes)
        uint magic = BitConverter.ToUInt32(raw, 0);
        uint version = BitConverter.ToUInt32(raw, 4);
        uint numPoints = BitConverter.ToUInt32(raw, 8);
        uint shFracFlags = BitConverter.ToUInt32(raw, 12);

        if (magic != 0x5053474E) // "NGSP"
            throw new Exception($"Invalid SPZ magic: 0x{magic:X8}");
        if (version < 2 || version > 3)
            throw new Exception($"Unsupported SPZ version: {version}");
        if (numPoints > 10_000_000)
            throw new Exception($"SPZ numPoints too large: {numPoints}");

        int shLevel = (int)(shFracFlags & 0xFF);
        int fractBits = (int)((shFracFlags >> 8) & 0xFF);
        float fractScale = 1.0f / (1 << fractBits);

        int[] shCoeffsTable = { 0, 3, 8, 15 };
        int shCoeffs = (shLevel >= 0 && shLevel <= 3) ? shCoeffsTable[shLevel] : 0;

        int n = (int)numPoints;
        // Validate data size: position(9) + alpha(1) + color(3) + scale(3) + rot(3) + sh(3*shCoeffs)
        int expectedBytes = 16 + n * (9 + 1 + 3 + 3 + 3 + 3 * shCoeffs);
        if (raw.Length < expectedBytes)
            throw new Exception($"SPZ file truncated: {raw.Length} < {expectedBytes}");

        // Structure-of-arrays layout after header
        int off = 16;
        int posOff = off;           off += n * 9;
        int alphaOff = off;         off += n * 1;
        int colorOff = off;         off += n * 3;
        int scaleOff = off;         off += n * 3;
        int rotOff = off;           off += n * 3;
        int shOff = off;

        var splats = new SplatData[n];

        for (int i = 0; i < n; i++)
        {
            ref SplatData s = ref splats[i];

            // Position: 3 × 24-bit signed integer, scaled by fractScale
            int pBase = posOff + i * 9;
            s.pos = new float3(
                SignExtend24(raw[pBase + 0] | (raw[pBase + 1] << 8) | (raw[pBase + 2] << 16)) * fractScale,
                SignExtend24(raw[pBase + 3] | (raw[pBase + 4] << 8) | (raw[pBase + 5] << 16)) * fractScale,
                SignExtend24(raw[pBase + 6] | (raw[pBase + 7] << 8) | (raw[pBase + 8] << 16)) * fractScale
            );

            // Alpha: 1 byte [0,255] → [0,1]
            s.opacity = raw[alphaOff + i] / 255f;

            // Color: 3 bytes RGB → SH DC0 space → SH0ToColor
            int cBase = colorOff + i * 3;
            float3 col = new float3(raw[cBase], raw[cBase + 1], raw[cBase + 2]) / 255f - 0.5f;
            col /= 0.15f; // back to SH coefficient space
            s.dc0 = GaussianUtils.SH0ToColor(col);

            // Scale: 3 bytes → log scale → exp
            int sBase = scaleOff + i * 3;
            float3 logScale = new float3(
                raw[sBase]     / 16f - 10f,
                raw[sBase + 1] / 16f - 10f,
                raw[sBase + 2] / 16f - 10f
            );
            s.scale = GaussianUtils.LinearScale(logScale);

            // Rotation: 3 bytes (xyz), derive w
            int rBase = rotOff + i * 3;
            float3 rxyz = new float3(
                raw[rBase]     / 127.5f - 1f,
                raw[rBase + 1] / 127.5f - 1f,
                raw[rBase + 2] / 127.5f - 1f
            );
            float rw = math.sqrt(math.max(0f, 1f - math.dot(rxyz, rxyz)));
            float4 q = math.normalize(new float4(rxyz, rw));
            s.rot = GaussianUtils.PackSmallest3Rotation(q);

            // SH coefficients
            if (shCoeffs > 0)
            {
                s.sh = new float3[15];
                int shBase = shOff + i * 3 * shCoeffs;
                for (int j = 0; j < shCoeffs && j < 15; j++)
                {
                    int b = shBase + j * 3;
                    s.sh[j] = new float3(
                        (raw[b]     - 128f) / 128f,
                        (raw[b + 1] - 128f) / 128f,
                        (raw[b + 2] - 128f) / 128f
                    );
                }
            }
        }

        return splats;
    }

    static int SignExtend24(int v)
    {
        return (v & 0x800000) != 0 ? v | unchecked((int)0xFF000000) : v;
    }

    // ── Morton Reorder ────────────────────────────────────────────────────────

    static void MortonReorder(SplatData[] splats, float3 bMin, float3 bMax)
    {
        float3 inv = 1f / math.max(bMax - bMin, 1e-10f);
        float kScaler = (1 << 21) - 1;

        var keys = new ulong[splats.Length];
        var indices = new int[splats.Length];
        for (int i = 0; i < splats.Length; i++)
        {
            float3 norm = (splats[i].pos - bMin) * inv * kScaler;
            uint3 ipos = (uint3)math.clamp(norm, 0, kScaler);
            keys[i] = GaussianUtils.MortonEncode3(ipos);
            indices[i] = i;
        }

        Array.Sort(keys, indices);

        var copy = new SplatData[splats.Length];
        Array.Copy(splats, copy, splats.Length);
        for (int i = 0; i < splats.Length; i++)
            splats[i] = copy[indices[i]];
    }

    // ── Data Packing (Float32 / VeryHigh quality) ─────────────────────────────

    static void WriteFloat(byte[] dst, int offset, float v)
    {
        var b = BitConverter.GetBytes(v);
        dst[offset]     = b[0];
        dst[offset + 1] = b[1];
        dst[offset + 2] = b[2];
        dst[offset + 3] = b[3];
    }

    static void WriteUint(byte[] dst, int offset, uint v)
    {
        dst[offset]     = (byte)(v);
        dst[offset + 1] = (byte)(v >> 8);
        dst[offset + 2] = (byte)(v >> 16);
        dst[offset + 3] = (byte)(v >> 24);
    }

    static byte[] PackPositions(SplatData[] splats)
    {
        var data = new byte[splats.Length * 12]; // 3 × float32
        for (int i = 0; i < splats.Length; i++)
        {
            int o = i * 12;
            WriteFloat(data, o,     splats[i].pos.x);
            WriteFloat(data, o + 4, splats[i].pos.y);
            WriteFloat(data, o + 8, splats[i].pos.z);
        }
        return data;
    }

    static byte[] PackOther(SplatData[] splats)
    {
        // 4 bytes packed rotation + 12 bytes Float32 scale = 16 bytes/splat
        var data = new byte[splats.Length * 16];
        for (int i = 0; i < splats.Length; i++)
        {
            int o = i * 16;

            // Rotation: 10.10.10.2 packed (values already in [0,1] from PackSmallest3)
            float4 r = splats[i].rot;
            uint enc = (uint)(r.x * 1023.5f) |
                       ((uint)(r.y * 1023.5f) << 10) |
                       ((uint)(r.z * 1023.5f) << 20) |
                       ((uint)(r.w * 3.5f) << 30);
            WriteUint(data, o, enc);

            // Scale: Float32
            WriteFloat(data, o + 4,  splats[i].scale.x);
            WriteFloat(data, o + 8,  splats[i].scale.y);
            WriteFloat(data, o + 12, splats[i].scale.z);
        }
        return data;
    }

    static byte[] PackColor(SplatData[] splats)
    {
        var (width, height) = GaussianSplatAsset.CalcTextureSize(splats.Length);
        int pixelCount = width * height;
        var data = new byte[pixelCount * 16]; // Float32x4 = 16 bytes/pixel

        for (int i = 0; i < splats.Length; i++)
        {
            int texIdx = SplatIndexToTextureIndex((uint)i);
            int o = texIdx * 16;

            // dc0 and opacity are already linearized (SH0ToColor/Sigmoid applied in reader)
            WriteFloat(data, o,      splats[i].dc0.x);
            WriteFloat(data, o + 4,  splats[i].dc0.y);
            WriteFloat(data, o + 8,  splats[i].dc0.z);
            WriteFloat(data, o + 12, splats[i].opacity);
        }
        return data;
    }

    static byte[] PackSH(SplatData[] splats)
    {
        // SHTableItemFloat32: 15×float3 + float3 padding = 192 bytes
        const int itemSize = 16 * 12; // 16 × float3 = 192 bytes
        var data = new byte[splats.Length * itemSize];

        for (int i = 0; i < splats.Length; i++)
        {
            if (splats[i].sh == null) continue;
            int baseOff = i * itemSize;
            for (int band = 0; band < 15; band++)
            {
                int o = baseOff + band * 12;
                WriteFloat(data, o,     splats[i].sh[band].x);
                WriteFloat(data, o + 4, splats[i].sh[band].y);
                WriteFloat(data, o + 8, splats[i].sh[band].z);
            }
            // padding float3 (3 floats) stays zero
        }
        return data;
    }

    // ── Morton texture tiling (matches GaussianSplatAssetCreator) ─────────────

    static int SplatIndexToTextureIndex(uint idx)
    {
        uint2 xy = GaussianUtils.DecodeMorton2D_16x16(idx & 0xFF);
        uint width = GaussianSplatAsset.kTextureWidth / 16;
        idx >>= 8;
        uint x = (idx % width) * 16 + xy.x;
        uint y = (idx / width) * 16 + xy.y;
        return (int)(y * GaussianSplatAsset.kTextureWidth + x);
    }
}
