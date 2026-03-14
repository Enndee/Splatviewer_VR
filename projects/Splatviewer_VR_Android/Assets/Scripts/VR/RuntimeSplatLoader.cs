// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using GaussianSplatting.Runtime;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Loads .ply, .spz, .sog, and .spx Gaussian Splat files at runtime,
/// creating a GaussianSplatAsset in memory and assigning it to a GaussianSplatRenderer.
/// Uses Float32 quality (no chunking) for maximum simplicity.
/// </summary>
public class RuntimeSplatLoader : MonoBehaviour
{
    [Tooltip("The GaussianSplatRenderer to load splats into.")]
    public GaussianSplatRenderer targetRenderer;

    GaussianSplatAsset _currentAsset;
    string _currentFilePath;
    readonly object _preloadLock = new object();
    readonly Dictionary<string, GaussianSplatAsset> _preloadedAssets = new Dictionary<string, GaussianSplatAsset>(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _desiredPreloadPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _preloadQueue = new List<string>();
    Task<PreparedRuntimeAsset> _activePreloadTask;
    string _activePreloadPath;
    long _preloadCachedBytes;
    long _preloadBudgetBytes;

    public string CurrentFilePath => _currentFilePath;

    /// <summary>Total bytes currently used by preloaded assets in RAM.</summary>
    public long PreloadCachedBytes { get { lock (_preloadLock) return _preloadCachedBytes; } }

    /// <summary>Maximum bytes allowed for the preload cache (0 = unlimited).</summary>
    public long PreloadBudgetBytes { get { lock (_preloadLock) return _preloadBudgetBytes; } }

    sealed class PreparedRuntimeAsset
    {
        public string filePath;
        public string assetName;
        public int splatCount;
        public float3 boundsMin;
        public float3 boundsMax;
        public byte[] posData;
        public byte[] otherData;
        public byte[] colorData;
        public byte[] shData;

        public long TotalBytes => (long)(posData?.Length ?? 0) + (otherData?.Length ?? 0) + (colorData?.Length ?? 0) + (shData?.Length ?? 0);
    }

    void Awake()
    {
        if (targetRenderer == null)
            targetRenderer = GetComponent<GaussianSplatRenderer>();
    }

    /// <summary>
    /// Set the RAM budget for preloading, as a fraction of total system RAM.
    /// E.g. 0.3 means 30% of system RAM. Pass 0 to disable budget limit.
    /// </summary>
    public void SetPreloadBudgetFraction(float fraction)
    {
        long totalRam = (long)SystemInfo.systemMemorySize * 1024L * 1024L; // MB → bytes
        lock (_preloadLock)
        {
            _preloadBudgetBytes = fraction > 0f ? (long)(totalRam * Mathf.Clamp01(fraction)) : 0;
        }
        Debug.Log($"[RuntimeSplatLoader] Preload budget set to {_preloadBudgetBytes / (1024 * 1024)}MB ({fraction:P0} of {SystemInfo.systemMemorySize}MB RAM)");
    }

    /// <summary>
    /// Estimate the in-memory size (bytes) of a splat file once decoded to Float32 quality.
    /// Uses file size as a heuristic since the exact splat count isn't known without reading.
    /// </summary>
    public static long EstimateAssetBytes(string filePath)
    {
        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists) return 0;
            string ext = fi.Extension.ToLowerInvariant();

            // Estimate splat count from file size:
            // PLY: ~244 bytes/splat on disk (with SH), decompressed ~232 bytes/splat in RAM
            // SPZ: ~22 bytes/splat compressed, ~232 bytes/splat in RAM
            // SPX: variable block-based, typically 3-5x compression
            // SOG: similar to SPZ
            // RAM per splat (Float32): pos(12) + other(16) + color(16) + SH(192) = 236 bytes
            const long ramPerSplat = 236;

            long diskSize = fi.Length;
            long estimatedSplats;

            if (ext == ".ply")
                estimatedSplats = diskSize / 244;      // rough PLY density
            else if (ext == ".spz")
                estimatedSplats = diskSize / 22;        // SPZ is heavily compressed
            else if (ext == ".spx")
                estimatedSplats = diskSize / 30;        // SPX block compression
            else if (ext == ".sog")
                estimatedSplats = diskSize / 25;        // SOG similar to SPZ
            else
                estimatedSplats = diskSize / 100;       // fallback

            return Math.Max(estimatedSplats, 1) * ramPerSplat;
        }
        catch
        {
            return 100L * 1024 * 1024; // fallback: assume 100MB
        }
    }

    public static bool IsSupportedFileExtension(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext == ".ply" || ext == ".spz" || ext == ".sog" || ext == ".spx";
    }

    public void SetPreloadTargets(IEnumerable<string> filePaths)
    {
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (filePaths != null)
        {
            foreach (string filePath in filePaths)
            {
                if (!string.IsNullOrWhiteSpace(filePath))
                    desired.Add(filePath);
            }
        }

        List<GaussianSplatAsset> staleAssets = null;
        List<string> staleAssetPaths = null;

        lock (_preloadLock)
        {
            _desiredPreloadPaths.Clear();
            foreach (string filePath in desired)
                _desiredPreloadPaths.Add(filePath);

            foreach (var kvp in _preloadedAssets)
            {
                if (_desiredPreloadPaths.Contains(kvp.Key) || PathsEqual(kvp.Key, _currentFilePath))
                    continue;

                staleAssets ??= new List<GaussianSplatAsset>();
                staleAssetPaths ??= new List<string>();
                staleAssets.Add(kvp.Value);
                staleAssetPaths.Add(kvp.Key);
            }
            if (staleAssetPaths != null)
            {
                foreach (string filePath in staleAssetPaths)
                {
                    _preloadedAssets.Remove(filePath);
                    _preloadCachedBytes -= EstimateAssetBytes(filePath);
                }
                if (_preloadCachedBytes < 0) _preloadCachedBytes = 0;
            }

            // Remove queued entries that are no longer desired
            _preloadQueue.RemoveAll(p => !_desiredPreloadPaths.Contains(p));
        }

        if (staleAssets != null)
        {
            foreach (var asset in staleAssets)
            {
                if (asset != null)
                    Destroy(asset);
            }
        }
    }

    public void BeginPreload(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || !IsSupportedFileExtension(filePath))
            return;

        lock (_preloadLock)
        {
            if (_preloadedAssets.ContainsKey(filePath) || PathsEqual(filePath, _currentFilePath))
                return;
            if (PathsEqual(filePath, _activePreloadPath))
                return;
            if (_preloadQueue.FindIndex(p => PathsEqual(p, filePath)) >= 0)
                return;

            _preloadQueue.Add(filePath);
        }
    }

    void StartNextPreload()
    {
        lock (_preloadLock)
        {
            if (_activePreloadTask != null)
                return;

            while (_preloadQueue.Count > 0)
            {
                string next = _preloadQueue[0];
                _preloadQueue.RemoveAt(0);

                // Skip if already cached, already current, or no longer desired
                if (_preloadedAssets.ContainsKey(next) || PathsEqual(next, _currentFilePath) || !_desiredPreloadPaths.Contains(next))
                    continue;

                // Check RAM budget before starting
                if (_preloadBudgetBytes > 0)
                {
                    long estimatedCost = EstimateAssetBytes(next);
                    if (_preloadCachedBytes + estimatedCost > _preloadBudgetBytes)
                    {
                        Debug.Log($"[RuntimeSplatLoader] Preload SKIPPED \"{Path.GetFileName(next)}\" — budget full ({_preloadCachedBytes / (1024 * 1024)}MB / {_preloadBudgetBytes / (1024 * 1024)}MB)");
                        continue;
                    }
                }

                _activePreloadPath = next;
                string fileName = Path.GetFileName(next);
                Debug.Log($"[RuntimeSplatLoader] Preload STARTED for \"{fileName}\"");
                _activePreloadTask = Task.Run(() =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var result = BuildPreparedRuntimeAsset(next);
                    Debug.Log($"[RuntimeSplatLoader] Preload background work DONE for \"{fileName}\" in {sw.ElapsedMilliseconds}ms");
                    return result;
                });
                return;
            }
        }
    }

    public void PumpCompletedPreloads(int maxToFinalize = 1)
    {
        // Check if the active background task has finished
        Task<PreparedRuntimeAsset> finishedTask = null;
        string finishedPath = null;

        lock (_preloadLock)
        {
            if (_activePreloadTask != null && _activePreloadTask.IsCompleted)
            {
                finishedTask = _activePreloadTask;
                finishedPath = _activePreloadPath;
                _activePreloadTask = null;
                _activePreloadPath = null;
            }
        }

        if (finishedTask != null)
        {
            PreparedRuntimeAsset prepared = null;
            Exception error = null;
            try
            {
                prepared = finishedTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                error = ex;
            }

            bool shouldKeep;
            lock (_preloadLock)
            {
                shouldKeep = _desiredPreloadPaths.Contains(finishedPath);
            }

            if (error != null)
            {
                Debug.LogWarning($"[RuntimeSplatLoader] Preload failed for {finishedPath}: {error.Message}");
            }
            else if (shouldKeep && prepared != null)
            {
                var asset = CreateRuntimeAsset(prepared);
                long assetBytes = prepared.TotalBytes;
                bool keepAsset = false;
                lock (_preloadLock)
                {
                    if (_desiredPreloadPaths.Contains(finishedPath) && !_preloadedAssets.ContainsKey(finishedPath))
                    {
                        _preloadedAssets.Add(finishedPath, asset);
                        _preloadCachedBytes += assetBytes;
                        keepAsset = true;
                    }
                }

                if (keepAsset)
                    Debug.Log($"[RuntimeSplatLoader] Preload FINALIZED \"{Path.GetFileName(finishedPath)}\" into cache");
                else
                    Destroy(asset);
            }
        }

        // Start next queued preload if nothing is in flight
        StartNextPreload();
    }

    /// <summary>Load a .ply, .spz, or bundled .sog file from disk and display it.</summary>
    public bool LoadFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Debug.LogError($"[RuntimeSplatLoader] File not found: {filePath}");
            return false;
        }

        if (!IsSupportedFileExtension(filePath))
        {
            Debug.LogError($"[RuntimeSplatLoader] Unsupported format: {Path.GetExtension(filePath)}");
            return false;
        }

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var asset = GetOrCreateAsset(filePath);
            if (asset == null)
                return false;

            AssignCurrentAsset(filePath, asset);
            Debug.Log($"[RuntimeSplatLoader] Loaded \"{asset.name}\" ({asset.splatCount:N0} splats) in {sw.ElapsedMilliseconds}ms");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RuntimeSplatLoader] Failed to load {filePath}: {ex}");
            return false;
        }
    }


    // ── Movie Mode ──────────────────────────────────────────────────────────

    GaussianSplatAsset[] _movieFrames;

    /// <summary>True while movie frames are loaded and ready.</summary>
    public bool IsMovieReady => _movieFrames != null && _movieFrames.Length > 0;

    /// <summary>Number of loaded movie frames.</summary>
    public int MovieFrameCount => _movieFrames?.Length ?? 0;

    /// <summary>
    /// Estimate total RAM needed to load all files. Returns bytes.
    /// </summary>
    public static long EstimateTotalBytes(IReadOnlyList<string> files)
    {
        long total = 0;
        for (int i = 0; i < files.Count; i++)
            total += EstimateAssetBytes(files[i]);
        return total;
    }

    /// <summary>
    /// Check whether there is enough free system RAM (estimated) to load all files.
    /// Returns (fitsInRam, estimatedBytes, availableBytes).
    /// </summary>
    public static (bool fits, long estimatedBytes, long availableBytes) CheckMovieRamFit(IReadOnlyList<string> files)
    {
        long estimated = EstimateTotalBytes(files);
        // Use 80% of total system RAM as the ceiling
        long available = (long)SystemInfo.systemMemorySize * 1024L * 1024L * 80L / 100L;
        return (estimated <= available, estimated, available);
    }

    /// <summary>
    /// Progressively load all files into movie frames on a background thread.
    /// Call PumpMovieLoad() each frame to finalize completed frames.
    /// The onProgress callback receives (loadedCount, totalCount).
    /// Returns false if loading cannot start.
    /// </summary>
    public bool BeginMovieLoad(IReadOnlyList<string> files, Action<int, int> onProgress)
    {
        StopMovie();

        _movieLoadFiles = new List<string>(files);
        _movieLoadTotal = files.Count;
        _movieLoadDone = 0;
        _movieLaunchNext = 0;
        _movieLoadFailed = false;
        _movieLoadProgress = onProgress;
        _movieLoadResults = new GaussianSplatAsset[files.Count];
        _movieLoadTasks = new Task<PreparedRuntimeAsset>[files.Count];
        _movieLoadStopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Sliding window: limit concurrent decodes to avoid CPU/memory thrashing.
        // Each decode (especially SOG/WebP) is very CPU + memory heavy,
        // so more than ~8 simultaneous decodes causes severe cache thrashing.
        _movieMaxConcurrency = Math.Min(16, Math.Max(1, Environment.ProcessorCount));
        int initialBatch = Math.Min(_movieMaxConcurrency, files.Count);
        for (int i = 0; i < initialBatch; i++)
            LaunchMovieTask(i);
        _movieLaunchNext = initialBatch;

        Debug.Log($"[RuntimeSplatLoader] Movie decode started: {files.Count} frames, {_movieMaxConcurrency} parallel workers");
        return true;
    }

    void LaunchMovieTask(int index)
    {
        string filePath = _movieLoadFiles[index];
        string fileName = Path.GetFileName(filePath);
        _movieLoadTasks[index] = Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = BuildPreparedRuntimeAsset(filePath);
            Debug.Log($"[RuntimeSplatLoader] Movie frame \"{fileName}\" decoded in {sw.ElapsedMilliseconds}ms");
            return result;
        });
    }

    /// <summary>
    /// Pump movie loading each frame. Returns true when all frames are loaded.
    /// </summary>
    public bool PumpMovieLoad()
    {
        if (_movieLoadFiles == null)
            return true; // nothing to do

        // Finalize as many completed frames as possible (in order)
        while (_movieLoadDone < _movieLoadTotal)
        {
            var task = _movieLoadTasks[_movieLoadDone];
            if (!task.IsCompleted) break;

            PreparedRuntimeAsset prepared = null;
            try
            {
                prepared = task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RuntimeSplatLoader] Movie load failed for frame {_movieLoadDone}: {ex.Message}");
                _movieLoadFailed = true;
            }

            if (prepared != null && !_movieLoadFailed)
            {
                _movieLoadResults[_movieLoadDone] = CreateRuntimeAsset(prepared);
            }

            _movieLoadDone++;
            _movieLoadProgress?.Invoke(_movieLoadDone, _movieLoadTotal);

            // Sliding window: launch next task to keep workers busy
            if (!_movieLoadFailed && _movieLaunchNext < _movieLoadTotal)
            {
                LaunchMovieTask(_movieLaunchNext);
                _movieLaunchNext++;
            }

            if (_movieLoadFailed) break;
        }

        if (_movieLoadFailed || _movieLoadDone >= _movieLoadTotal)
        {
            // Finished (or failed)
            if (!_movieLoadFailed)
            {
                _movieFrames = _movieLoadResults;
                _movieLoadStopwatch?.Stop();
                long elapsedMs = _movieLoadStopwatch?.ElapsedMilliseconds ?? 0;
                float elapsedSec = elapsedMs / 1000f;
                float avgMs = _movieFrames.Length > 0 ? (float)elapsedMs / _movieFrames.Length : 0;
                Debug.Log($"[RuntimeSplatLoader] Movie loaded: {_movieFrames.Length} frames in {elapsedSec:F1}s ({avgMs:F0}ms/frame avg, {_movieMaxConcurrency} workers)");
            }
            else
            {
                // Clean up partial results
                for (int i = 0; i < _movieLoadResults.Length; i++)
                {
                    if (_movieLoadResults[i] != null)
                        Destroy(_movieLoadResults[i]);
                }
                _movieLoadResults = null;
            }

            _movieLoadFiles = null;
            _movieLoadTasks = null;
            _movieLoadProgress = null;
            return true;
        }

        return false;
    }

    /// <summary>Show a specific movie frame by index (instant swap, no loading).</summary>
    public void ShowMovieFrame(int frameIndex)
    {
        if (_movieFrames == null || frameIndex < 0 || frameIndex >= _movieFrames.Length)
            return;

        var asset = _movieFrames[frameIndex];
        if (asset == null) return;

        _currentAsset = asset;
        _currentFilePath = null;
        targetRenderer.m_Asset = asset;
    }

    /// <summary>Release all movie frames from RAM.</summary>
    public void StopMovie()
    {
        if (_movieFrames != null)
        {
            for (int i = 0; i < _movieFrames.Length; i++)
            {
                if (_movieFrames[i] != null && !ReferenceEquals(_movieFrames[i], _currentAsset))
                    Destroy(_movieFrames[i]);
            }
            _movieFrames = null;
            Debug.Log("[RuntimeSplatLoader] Movie frames released");
        }

        // Cancel any in-progress movie load
        _movieLoadFiles = null;
        _movieLoadTasks = null;
        _movieLoadResults = null;
        _movieLoadProgress = null;
    }

    // Movie loading state
    List<string> _movieLoadFiles;
    GaussianSplatAsset[] _movieLoadResults;
    Task<PreparedRuntimeAsset>[] _movieLoadTasks;
    Action<int, int> _movieLoadProgress;
    int _movieLoadTotal;
    int _movieLoadDone;
    int _movieLaunchNext;
    int _movieMaxConcurrency;
    bool _movieLoadFailed;
    System.Diagnostics.Stopwatch _movieLoadStopwatch;

    // ── End Movie Mode ────────────────────────────────────────────────────────


    void OnDestroy()
    {
        StopMovie();

        var toDestroy = new List<GaussianSplatAsset>();
        lock (_preloadLock)
        {
            foreach (var asset in _preloadedAssets.Values)
            {
                if (asset != null && !toDestroy.Contains(asset))
                    toDestroy.Add(asset);
            }
            _preloadedAssets.Clear();
            _preloadQueue.Clear();
            _desiredPreloadPaths.Clear();
            _activePreloadTask = null;
            _activePreloadPath = null;
            _preloadCachedBytes = 0;
        }

        if (_currentAsset != null && !toDestroy.Contains(_currentAsset))
            toDestroy.Add(_currentAsset);

        foreach (var asset in toDestroy)
        {
            if (asset != null)
                Destroy(asset);
        }
    }

    GaussianSplatAsset GetOrCreateAsset(string filePath)
    {
        string fileName = Path.GetFileName(filePath);
        GaussianSplatAsset cachedAsset;
        lock (_preloadLock)
        {
            if (_preloadedAssets.TryGetValue(filePath, out cachedAsset))
            {
                Debug.Log($"[RuntimeSplatLoader] CACHE HIT for \"{fileName}\" — returning preloaded asset");
                return cachedAsset;
            }
        }

        // Check if this file is the one currently being preloaded
        Task<PreparedRuntimeAsset> activeTask = null;
        lock (_preloadLock)
        {
            if (PathsEqual(filePath, _activePreloadPath) && _activePreloadTask != null)
            {
                activeTask = _activePreloadTask;
                _activePreloadTask = null;
                _activePreloadPath = null;
            }
        }

        PreparedRuntimeAsset prepared;
        if (activeTask != null)
        {
            bool alreadyDone = activeTask.IsCompleted;
            Debug.Log($"[RuntimeSplatLoader] TASK WAIT for \"{fileName}\" (already completed: {alreadyDone})");
            prepared = activeTask.GetAwaiter().GetResult();
        }
        else
        {
            Debug.Log($"[RuntimeSplatLoader] SYNC BUILD for \"{fileName}\" — no preload available");
            prepared = BuildPreparedRuntimeAsset(filePath);
        }

        if (prepared == null)
            return null;

        var asset = CreateRuntimeAsset(prepared);
        bool cacheAsset;
        GaussianSplatAsset duplicateAsset = null;
        lock (_preloadLock)
        {
            cacheAsset = _desiredPreloadPaths.Contains(filePath);
            if (cacheAsset && !_preloadedAssets.ContainsKey(filePath))
                _preloadedAssets.Add(filePath, asset);
            else if (_preloadedAssets.TryGetValue(filePath, out cachedAsset))
            {
                cacheAsset = false;
                duplicateAsset = asset;
                asset = cachedAsset;
            }
        }

        if (duplicateAsset != null)
            Destroy(duplicateAsset);

        return asset;
    }

    static PreparedRuntimeAsset BuildPreparedRuntimeAsset(string filePath)
    {
        var swStep = System.Diagnostics.Stopwatch.StartNew();

        var splats = ReadInputSplats(filePath);
        if (splats == null || splats.Length == 0)
            return null;
        long msRead = swStep.ElapsedMilliseconds;

        swStep.Restart();
        MortonReorder(splats, out float3 bMin, out float3 bMax);
        long msMorton = swStep.ElapsedMilliseconds;

        swStep.Restart();
        var result = new PreparedRuntimeAsset
        {
            filePath = filePath,
            assetName = Path.GetFileNameWithoutExtension(filePath),
            splatCount = splats.Length,
            boundsMin = bMin,
            boundsMax = bMax,
            posData = PackPositions(splats),
            otherData = PackOther(splats),
            colorData = PackColor(splats),
            shData = PackSH(splats)
        };
        long msPack = swStep.ElapsedMilliseconds;

        Debug.Log($"[RuntimeSplatLoader] \"{Path.GetFileName(filePath)}\" breakdown: read={msRead}ms morton={msMorton}ms pack={msPack}ms ({splats.Length:N0} splats)");
        return result;
    }

    static SplatData[] ReadInputSplats(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext == ".spz"
            ? ReadSpz(filePath)
            : ext == ".sog"
                ? PlayCanvasSogReader.ReadFile(filePath)
                : ext == ".spx"
                    ? SpxReader.ReadFile(filePath)
                    : ReadPly(filePath);
    }

    static GaussianSplatAsset CreateRuntimeAsset(PreparedRuntimeAsset prepared)
    {
        var asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
        asset.Initialize(
            prepared.splatCount,
            GaussianSplatAsset.VectorFormat.Float32,
            GaussianSplatAsset.VectorFormat.Float32,
            GaussianSplatAsset.ColorFormat.Float32x4,
            GaussianSplatAsset.SHFormat.Float32,
            (Vector3)prepared.boundsMin, (Vector3)prepared.boundsMax, null
        );
        asset.name = prepared.assetName;
        asset.runtimePosData = prepared.posData;
        asset.runtimeOtherData = prepared.otherData;
        asset.runtimeColorData = prepared.colorData;
        asset.runtimeSHData = prepared.shData;
        asset.SetDataHash(new Hash128((uint)prepared.splatCount, (uint)GaussianSplatAsset.kCurrentVersion, 0, (uint)prepared.filePath.GetHashCode()));
        return asset;
    }

    void AssignCurrentAsset(string filePath, GaussianSplatAsset asset)
    {
        if (_currentAsset != null && !ReferenceEquals(_currentAsset, asset) && !IsCachedAsset(_currentFilePath, _currentAsset))
            Destroy(_currentAsset);

        _currentAsset = asset;
        _currentFilePath = filePath;
        targetRenderer.m_Asset = _currentAsset;
    }

    bool IsCachedAsset(string filePath, GaussianSplatAsset asset)
    {
        if (string.IsNullOrWhiteSpace(filePath) || asset == null)
            return false;

        lock (_preloadLock)
        {
            return _preloadedAssets.TryGetValue(filePath, out var cachedAsset) && ReferenceEquals(cachedAsset, asset);
        }
    }

    static bool PathsEqual(string lhs, string rhs)
    {
        return string.Equals(lhs, rhs, StringComparison.OrdinalIgnoreCase);
    }

    // ── PLY Reader ────────────────────────────────────────────────────────────

    internal struct SplatData
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

        // Pre-resolve property indices (avoids per-vertex dictionary lookups + string allocations)
        int iX = propIdx.TryGetValue("x", out int _pi) ? _pi : -1;
        int iY = propIdx.TryGetValue("y", out _pi) ? _pi : -1;
        int iZ = propIdx.TryGetValue("z", out _pi) ? _pi : -1;
        int iDc0 = propIdx.TryGetValue("f_dc_0", out _pi) ? _pi : -1;
        int iDc1 = propIdx.TryGetValue("f_dc_1", out _pi) ? _pi : -1;
        int iDc2 = propIdx.TryGetValue("f_dc_2", out _pi) ? _pi : -1;
        int iOpacity = propIdx.TryGetValue("opacity", out _pi) ? _pi : -1;
        int iScale0 = propIdx.TryGetValue("scale_0", out _pi) ? _pi : -1;
        int iScale1 = propIdx.TryGetValue("scale_1", out _pi) ? _pi : -1;
        int iScale2 = propIdx.TryGetValue("scale_2", out _pi) ? _pi : -1;
        int iRot0 = propIdx.TryGetValue("rot_0", out _pi) ? _pi : -1;
        int iRot1 = propIdx.TryGetValue("rot_1", out _pi) ? _pi : -1;
        int iRot2 = propIdx.TryGetValue("rot_2", out _pi) ? _pi : -1;
        int iRot3 = propIdx.TryGetValue("rot_3", out _pi) ? _pi : -1;

        int[] shIdx = null;
        if (shCount > 0)
        {
            shIdx = new int[45];
            for (int j = 0; j < 45; j++)
                shIdx[j] = propIdx.TryGetValue($"f_rest_{j}", out _pi) ? _pi : -1;
        }

        // Read all binary vertex data in one bulk read (avoids ~N individual Read calls)
        int vertexBytes = stride * 4;
        byte[] allVertexData = new byte[(long)vertexCount * vertexBytes];
        int totalBytesRead = 0;
        while (totalBytesRead < allVertexData.Length)
        {
            int r = fs.Read(allVertexData, totalBytesRead, allVertexData.Length - totalBytesRead);
            if (r == 0) throw new Exception($"PLY truncated: read {totalBytesRead} of {allVertexData.Length} bytes");
            totalBytesRead += r;
        }

        var splats = new SplatData[vertexCount];
        var floatBuf = new float[stride];

        for (int v = 0; v < vertexCount; v++)
        {
            Buffer.BlockCopy(allVertexData, v * vertexBytes, floatBuf, 0, vertexBytes);

            ref SplatData s = ref splats[v];

            s.pos = new float3(
                iX >= 0 ? floatBuf[iX] : 0f,
                iY >= 0 ? floatBuf[iY] : 0f,
                iZ >= 0 ? floatBuf[iZ] : 0f
            );

            float3 rawDc0 = new float3(
                iDc0 >= 0 ? floatBuf[iDc0] : 0f,
                iDc1 >= 0 ? floatBuf[iDc1] : 0f,
                iDc2 >= 0 ? floatBuf[iDc2] : 0f
            );
            s.dc0 = GaussianUtils.SH0ToColor(rawDc0);

            s.opacity = GaussianUtils.Sigmoid(iOpacity >= 0 ? floatBuf[iOpacity] : 0f);

            s.scale = GaussianUtils.LinearScale(new float3(
                iScale0 >= 0 ? floatBuf[iScale0] : 0f,
                iScale1 >= 0 ? floatBuf[iScale1] : 0f,
                iScale2 >= 0 ? floatBuf[iScale2] : 0f
            ));

            float4 q = new float4(
                iRot0 >= 0 ? floatBuf[iRot0] : 1f,
                iRot1 >= 0 ? floatBuf[iRot1] : 0f,
                iRot2 >= 0 ? floatBuf[iRot2] : 0f,
                iRot3 >= 0 ? floatBuf[iRot3] : 0f
            );
            q = GaussianUtils.NormalizeSwizzleRotation(q);
            s.rot = GaussianUtils.PackSmallest3Rotation(q);

            if (shIdx != null)
            {
                s.sh = new float3[15];
                for (int j = 0; j < 15; j++)
                {
                    float sr = (j < shCount && shIdx[j] >= 0) ? floatBuf[shIdx[j]] : 0f;
                    float sg = (j + 15 < shCount && shIdx[j + 15] >= 0) ? floatBuf[shIdx[j + 15]] : 0f;
                    float sb = (j + 30 < shCount && shIdx[j + 30] >= 0) ? floatBuf[shIdx[j + 30]] : 0f;
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

    static void MortonReorder(SplatData[] splats, out float3 bMin, out float3 bMax)
    {
        // Compute bounds while building Morton keys (folds two O(N) passes into one)
        bMin = float.PositiveInfinity;
        bMax = float.NegativeInfinity;
        var keys = new ulong[splats.Length];
        var indices = new int[splats.Length];
        for (int i = 0; i < splats.Length; i++)
        {
            float3 p = splats[i].pos;
            bMin = math.min(bMin, p);
            bMax = math.max(bMax, p);
            indices[i] = i;
        }

        float3 inv = 1f / math.max(bMax - bMin, 1e-10f);
        float kScaler = (1 << 21) - 1;
        for (int i = 0; i < splats.Length; i++)
        {
            float3 norm = (splats[i].pos - bMin) * inv * kScaler;
            uint3 ipos = (uint3)math.clamp(norm, 0, kScaler);
            keys[i] = GaussianUtils.MortonEncode3(ipos);
        }

        Array.Sort(keys, indices);

        // In-place cycle permutation — avoids allocating a full SplatData[N] copy
        for (int i = 0; i < splats.Length; i++)
        {
            while (indices[i] != i)
            {
                int target = indices[i];
                (splats[i], splats[target]) = (splats[target], splats[i]);
                (indices[i], indices[target]) = (indices[target], target);
            }
        }
    }

    // ── Data Packing (Float32 / VeryHigh quality) ─────────────────────────────
    // Uses Buffer.BlockCopy for bulk float→byte conversion to avoid
    // per-field BitConverter.GetBytes allocations (was ~13M allocs per file).

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
    struct FloatUintUnion
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public float f;
        [System.Runtime.InteropServices.FieldOffset(0)] public uint u;
    }

    static byte[] PackPositions(SplatData[] splats)
    {
        int n = splats.Length;
        var buf = new float[n * 3];
        for (int i = 0; i < n; i++)
        {
            int o = i * 3;
            buf[o]     = splats[i].pos.x;
            buf[o + 1] = splats[i].pos.y;
            buf[o + 2] = splats[i].pos.z;
        }
        var data = new byte[n * 12];
        Buffer.BlockCopy(buf, 0, data, 0, data.Length);
        return data;
    }

    static byte[] PackOther(SplatData[] splats)
    {
        int n = splats.Length;
        // Layout per splat: uint32 rotation + float32×3 scale = 4 × uint32 = 16 bytes
        var buf = new uint[n * 4];
        FloatUintUnion fu;
        fu.u = 0;
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            float4 r = splats[i].rot;
            buf[o] = (uint)(r.x * 1023.5f) |
                     ((uint)(r.y * 1023.5f) << 10) |
                     ((uint)(r.z * 1023.5f) << 20) |
                     ((uint)(r.w * 3.5f) << 30);
            fu.f = splats[i].scale.x; buf[o + 1] = fu.u;
            fu.f = splats[i].scale.y; buf[o + 2] = fu.u;
            fu.f = splats[i].scale.z; buf[o + 3] = fu.u;
        }
        var data = new byte[n * 16];
        Buffer.BlockCopy(buf, 0, data, 0, data.Length);
        return data;
    }

    static byte[] PackColor(SplatData[] splats)
    {
        var (width, height) = GaussianSplatAsset.CalcTextureSize(splats.Length);
        int pixelCount = width * height;
        var buf = new float[pixelCount * 4];
        for (int i = 0; i < splats.Length; i++)
        {
            int texIdx = SplatIndexToTextureIndex((uint)i);
            int o = texIdx * 4;
            buf[o]     = splats[i].dc0.x;
            buf[o + 1] = splats[i].dc0.y;
            buf[o + 2] = splats[i].dc0.z;
            buf[o + 3] = splats[i].opacity;
        }
        var data = new byte[pixelCount * 16];
        Buffer.BlockCopy(buf, 0, data, 0, data.Length);
        return data;
    }

    static byte[] PackSH(SplatData[] splats)
    {
        // SHTableItemFloat32: 15×float3 + float3 padding = 48 floats = 192 bytes
        const int floatsPerItem = 48;
        var buf = new float[splats.Length * floatsPerItem];
        for (int i = 0; i < splats.Length; i++)
        {
            if (splats[i].sh == null) continue;
            int baseOff = i * floatsPerItem;
            for (int band = 0; band < 15; band++)
            {
                int o = baseOff + band * 3;
                buf[o]     = splats[i].sh[band].x;
                buf[o + 1] = splats[i].sh[band].y;
                buf[o + 2] = splats[i].sh[band].z;
            }
        }
        var data = new byte[splats.Length * floatsPerItem * 4];
        Buffer.BlockCopy(buf, 0, data, 0, data.Length);
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
