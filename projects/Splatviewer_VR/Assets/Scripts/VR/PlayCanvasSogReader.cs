// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using GaussianSplatting.Runtime;
using Unity.Mathematics;
using UnityEngine;
using WebP;

internal static class PlayCanvasSogReader
{
    static readonly int[] s_shCoeffCounts = { 0, 3, 8, 15 };

    public static RuntimeSplatLoader.SplatData[] ReadFile(string filePath)
    {
        using var archive = ZipFile.OpenRead(filePath);
        return ReadArchive(archive, filePath);
    }

    static RuntimeSplatLoader.SplatData[] ReadArchive(ZipArchive archive, string filePath)
    {
        var entries = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .ToDictionary(entry => NormalizeEntryName(entry.FullName), StringComparer.OrdinalIgnoreCase);

        byte[] metaBytes = ReadArchiveEntry(entries, "meta.json");
        var meta = JsonUtility.FromJson<SogMeta>(Encoding.UTF8.GetString(metaBytes));
        if (meta == null)
            throw new Exception($"SOG meta.json could not be parsed: {filePath}");
        if (meta.version != 2)
            throw new Exception($"Unsupported SOG version {meta.version} in {filePath}; only version 2 is supported.");
        if (meta.count <= 0)
            throw new Exception($"SOG meta.count must be positive in {filePath}");

        DecodedImage meansLower = LoadImage(entries, meta.means?.files, 0, "means lower");
        DecodedImage meansUpper = LoadImage(entries, meta.means?.files, 1, "means upper");
        DecodedImage scales = LoadImage(entries, meta.scales?.files, 0, "scales");
        DecodedImage quats = LoadImage(entries, meta.quats?.files, 0, "quats");
        DecodedImage sh0 = LoadImage(entries, meta.sh0?.files, 0, "sh0");

        ValidateMainImageDimensions(meansLower, meansUpper, scales, quats, sh0, filePath);

        int width = meansLower.width;
        int height = meansLower.height;
        if (meta.count > width * height)
            throw new Exception($"SOG meta.count {meta.count} exceeds image capacity {width * height} in {filePath}");

        ValidateCodebook(meta.means?.mins, 3, "means.mins");
        ValidateCodebook(meta.means?.maxs, 3, "means.maxs");
        ValidateCodebook(meta.scales?.codebook, 256, "scales.codebook");
        ValidateCodebook(meta.sh0?.codebook, 256, "sh0.codebook");

        bool hasHigherOrderSh = HasHigherOrderSh(meta);
        int shCoeffCount = 0;
        DecodedImage shLabels = default;
        DecodedImage shCentroids = default;
        if (hasHigherOrderSh)
        {
            if (meta.shN.bands < 1 || meta.shN.bands > 3)
                throw new Exception($"SOG shN.bands must be 1..3 in {filePath}");
            if (meta.shN.count < 1 || meta.shN.count > 65536)
                throw new Exception($"SOG shN.count must be 1..65536 in {filePath}");

            shCoeffCount = s_shCoeffCounts[meta.shN.bands];
            ValidateCodebook(meta.shN.codebook, 256, "shN.codebook");

            shCentroids = LoadImage(entries, meta.shN.files, 0, "shN centroids");
            shLabels = LoadImage(entries, meta.shN.files, 1, "shN labels");
            ValidateMatchingDimensions(shLabels, width, height, "shN labels", filePath);

            int requiredCentroidWidth = 64 * shCoeffCount;
            int requiredCentroidHeight = (meta.shN.count + 63) / 64;
            if (shCentroids.width < requiredCentroidWidth || shCentroids.height < requiredCentroidHeight)
            {
                throw new Exception(
                    $"SOG shN centroid image is too small in {filePath}. Expected at least {requiredCentroidWidth}x{requiredCentroidHeight}, got {shCentroids.width}x{shCentroids.height}");
            }
        }

        int count = meta.count;
        var splats = new RuntimeSplatLoader.SplatData[count];
        for (int index = 0; index < count; index++)
        {
            int x = index % width;
            int y = index / width;

            Rgba32 meansL = GetPixelTopLeft(meansLower, x, y);
            Rgba32 meansU = GetPixelTopLeft(meansUpper, x, y);
            Rgba32 scalesPixel = GetPixelTopLeft(scales, x, y);
            Rgba32 quatPixel = GetPixelTopLeft(quats, x, y);
            Rgba32 sh0Pixel = GetPixelTopLeft(sh0, x, y);

            ref RuntimeSplatLoader.SplatData splat = ref splats[index];
            splat.pos = DecodePosition(meta, meansL, meansU);
            splat.scale = DecodeScale(meta, scalesPixel);
            splat.rot = DecodeQuaternion(quatPixel);
            splat.dc0 = DecodeDc(meta, sh0Pixel);
            splat.opacity = sh0Pixel.a / 255f;

            if (hasHigherOrderSh)
                splat.sh = DecodeHigherOrderSh(meta, shLabels, shCentroids, x, y, shCoeffCount);
        }

        return splats;
    }

    static string NormalizeEntryName(string entryName)
    {
        return entryName.Replace('\\', '/').TrimStart('.', '/');
    }

    static byte[] ReadArchiveEntry(Dictionary<string, ZipArchiveEntry> entries, string entryName)
    {
        if (!entries.TryGetValue(entryName, out ZipArchiveEntry entry))
        {
            entry = entries.Values.FirstOrDefault(candidate =>
                string.Equals(Path.GetFileName(candidate.FullName), entryName, StringComparison.OrdinalIgnoreCase));
        }

        if (entry == null)
            throw new FileNotFoundException($"Missing required SOG entry: {entryName}");

        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    static DecodedImage LoadImage(Dictionary<string, ZipArchiveEntry> entries, string[] files, int index, string label)
    {
        if (files == null || files.Length <= index || string.IsNullOrWhiteSpace(files[index]))
            throw new Exception($"SOG meta entry for {label} is missing a filename.");

        string fileName = files[index];
        byte[] bytes = ReadArchiveEntry(entries, NormalizeEntryName(fileName));
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext != ".webp")
            throw new Exception($"SOG image {fileName} uses unsupported runtime format {ext}. Only lossless WebP is currently supported.");

        byte[] rgba = RuntimeSplatLoader.DecodeWebPImage(bytes, out int width, out int height, fileName);

        return new DecodedImage(width, height, rgba);
    }

    static void ValidateMainImageDimensions(DecodedImage meansLower, DecodedImage meansUpper, DecodedImage scales, DecodedImage quats, DecodedImage sh0, string filePath)
    {
        ValidateMatchingDimensions(meansUpper, meansLower.width, meansLower.height, "means upper", filePath);
        ValidateMatchingDimensions(scales, meansLower.width, meansLower.height, "scales", filePath);
        ValidateMatchingDimensions(quats, meansLower.width, meansLower.height, "quats", filePath);
        ValidateMatchingDimensions(sh0, meansLower.width, meansLower.height, "sh0", filePath);
    }

    static void ValidateMatchingDimensions(DecodedImage image, int width, int height, string label, string filePath)
    {
        if (image.width != width || image.height != height)
            throw new Exception($"SOG {label} image dimensions do not match {width}x{height} in {filePath}; got {image.width}x{image.height}");
    }

    static void ValidateCodebook(float[] values, int expectedLength, string label)
    {
        if (values == null || values.Length != expectedLength)
            throw new Exception($"SOG {label} must contain {expectedLength} entries.");
    }

    static bool HasHigherOrderSh(SogMeta meta)
    {
        return meta.shN != null
            && meta.shN.count > 0
            && meta.shN.bands > 0
            && meta.shN.codebook != null
            && meta.shN.files != null
            && meta.shN.files.Length >= 2
            && !string.IsNullOrWhiteSpace(meta.shN.files[0])
            && !string.IsNullOrWhiteSpace(meta.shN.files[1]);
    }

    static float3 DecodePosition(SogMeta meta, Rgba32 meansL, Rgba32 meansU)
    {
        int qx = (meansU.r << 8) | meansL.r;
        int qy = (meansU.g << 8) | meansL.g;
        int qz = (meansU.b << 8) | meansL.b;

        float nx = math.lerp(meta.means.mins[0], meta.means.maxs[0], qx / 65535f);
        float ny = math.lerp(meta.means.mins[1], meta.means.maxs[1], qy / 65535f);
        float nz = math.lerp(meta.means.mins[2], meta.means.maxs[2], qz / 65535f);
        return new float3(Unlog(nx), Unlog(ny), Unlog(nz));
    }

    static float Unlog(float value)
    {
        return math.sign(value) * (math.exp(math.abs(value)) - 1f);
    }

    static float3 DecodeScale(SogMeta meta, Rgba32 scalesPixel)
    {
        float3 logScale = new float3(
            meta.scales.codebook[scalesPixel.r],
            meta.scales.codebook[scalesPixel.g],
            meta.scales.codebook[scalesPixel.b]);
        return GaussianUtils.LinearScale(logScale);
    }

    static float4 DecodeQuaternion(Rgba32 quatPixel)
    {
        int mode = quatPixel.a - 252;
        if (mode < 0 || mode > 3)
            throw new Exception($"Invalid SOG quaternion mode {quatPixel.a}; expected alpha in 252..255.");

        float a = DecodeQuatComponent(quatPixel.r);
        float b = DecodeQuatComponent(quatPixel.g);
        float c = DecodeQuatComponent(quatPixel.b);
        float omitted = math.sqrt(math.max(0f, 1f - (a * a + b * b + c * c)));

        // SOG stores quaternion components in (w,x,y,z) order with mode indicating
        // which was largest/omitted: 0=w, 1=x, 2=y, 3=z.
        // The remaining 3 are stored in order: a,b,c = next components after the gap.
        // PackSmallest3Rotation expects (x,y,z,w) order.
        float4 q = mode switch
        {
            0 => new float4(a, b, c, omitted),       // w omitted; a=x, b=y, c=z → (x, y, z, w)
            1 => new float4(omitted, b, c, a),        // x omitted; a=w, b=y, c=z → (x, y, z, w)
            2 => new float4(b, omitted, c, a),        // y omitted; a=w, b=x, c=z → (x, y, z, w)
            3 => new float4(b, c, omitted, a),        // z omitted; a=w, b=x, c=y → (x, y, z, w)
            _ => throw new InvalidOperationException(),
        };
        return GaussianUtils.PackSmallest3Rotation(math.normalize(q));
    }

    static float DecodeQuatComponent(byte value)
    {
        return ((value / 255f) - 0.5f) * (2f / math.SQRT2);
    }

    static float3 DecodeDc(SogMeta meta, Rgba32 sh0Pixel)
    {
        float3 dc0 = new float3(
            meta.sh0.codebook[sh0Pixel.r],
            meta.sh0.codebook[sh0Pixel.g],
            meta.sh0.codebook[sh0Pixel.b]);
        return GaussianUtils.SH0ToColor(dc0);
    }

    static float3[] DecodeHigherOrderSh(SogMeta meta, DecodedImage shLabels, DecodedImage shCentroids, int x, int y, int shCoeffCount)
    {
        Rgba32 labelPixel = GetPixelTopLeft(shLabels, x, y);
        int label = labelPixel.r | (labelPixel.g << 8);
        if (label < 0 || label >= meta.shN.count)
            throw new Exception($"SOG SH label {label} is out of range for palette size {meta.shN.count}.");

        int paletteX = label % 64;
        int paletteY = label / 64;
        var sh = new float3[15];
        for (int coeff = 0; coeff < shCoeffCount; coeff++)
        {
            int centroidX = paletteX * shCoeffCount + coeff;
            Rgba32 centroidPixel = GetPixelTopLeft(shCentroids, centroidX, paletteY);
            sh[coeff] = new float3(
                meta.shN.codebook[centroidPixel.r],
                meta.shN.codebook[centroidPixel.g],
                meta.shN.codebook[centroidPixel.b]);
        }
        return sh;
    }

    static Rgba32 GetPixelTopLeft(DecodedImage image, int x, int y)
    {
        if (x < 0 || x >= image.width || y < 0 || y >= image.height)
            throw new IndexOutOfRangeException($"Pixel ({x}, {y}) is outside {image.width}x{image.height}");

        int sourceRow = image.height - 1 - y;
        int offset = (sourceRow * image.width + x) * 4;
        return new Rgba32(
            image.rgba[offset],
            image.rgba[offset + 1],
            image.rgba[offset + 2],
            image.rgba[offset + 3]);
    }

    readonly struct DecodedImage
    {
        public readonly int width;
        public readonly int height;
        public readonly byte[] rgba;

        public DecodedImage(int width, int height, byte[] rgba)
        {
            this.width = width;
            this.height = height;
            this.rgba = rgba;
        }
    }

    readonly struct Rgba32
    {
        public readonly byte r;
        public readonly byte g;
        public readonly byte b;
        public readonly byte a;

        public Rgba32(byte r, byte g, byte b, byte a)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }
    }

    [Serializable]
    sealed class SogMeta
    {
        public int version;
        public int count;
        public bool antialias;
        public MeansSection means;
        public CodebookSection scales;
        public FileSection quats;
        public CodebookSection sh0;
        public ShNSection shN;
    }

    [Serializable]
    sealed class MeansSection
    {
        public float[] mins;
        public float[] maxs;
        public string[] files;
    }

    [Serializable]
    class FileSection
    {
        public string[] files;
    }

    [Serializable]
    class CodebookSection : FileSection
    {
        public float[] codebook;
    }

    [Serializable]
    sealed class ShNSection : CodebookSection
    {
        public int count;
        public int bands;
    }
}