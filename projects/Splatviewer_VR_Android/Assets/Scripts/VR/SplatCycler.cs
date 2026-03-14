// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Cycles through supported splat files in a folder using VR controller buttons.
///
/// VR Controls (right controller):
///   B (secondaryButton) → next splat file
///   A (primaryButton)   → previous splat file
///
/// Keyboard fallback:
///   R → next
///   F → previous
///
/// Also accepts PageDown/PageUp/N/P as legacy shortcuts.
/// </summary>
public class SplatCycler : MonoBehaviour
{
    [Header("Setup")]
    [Tooltip("The RuntimeSplatLoader to use for loading files.")]
    public RuntimeSplatLoader loader;

    [Tooltip("Folder to scan for .ply files. Leave empty to use a default path.")]
    public string splatFolder = "";

    [Tooltip("Auto-load the first file on start.")]
    public bool autoLoadFirst = true;

    [Header("Preloading")]
    [Tooltip("Preload upcoming splat files into RAM so cycling feels faster.")]
    public bool preloadUpcomingFiles = true;

    [Tooltip("Fraction of system RAM to use for preloading (e.g. 0.3 = 30%). Set to 0 to disable budget limit.")]
    [Range(0f, 0.8f)] public float preloadRamFraction = 0.3f;

    [Tooltip("How many completed preload jobs to finalize each frame.")]
    [Min(1)] public int finalizePreloadsPerFrame = 1;

    [Header("Movie Mode")]
    [Tooltip("Playback FPS for movie mode.")]
    [Range(1, 60)] public int movieFps = 10;

    [Header("Status (read-only)")]
    [SerializeField] string _currentFile = "(none)";
    [SerializeField] int _currentIndex = -1;
    [SerializeField] int _totalFiles;

    // Internal
    List<string> _files = new List<string>();
    bool _btnNextReady = true;
    bool _btnPrevReady = true;
    VRFileBrowser _browser;
    VRRig _rig;

    // Movie mode
    bool _moviePlaying;
    int _movieFrame;
    float _movieTimer;

    public bool IsMoviePlaying => _moviePlaying;
    public bool IsMovieReady => loader != null && loader.IsMovieReady;

    void Start()
    {
        if (loader == null)
            loader = GetComponent<RuntimeSplatLoader>();
        _browser = FindAnyObjectByType<VRFileBrowser>();
        _rig = FindAnyObjectByType<VRRig>();

        if (string.IsNullOrEmpty(splatFolder))
            splatFolder = QuestSplatStorage.GetRootFolder();

        ApplyPreloadBudget();

        if (string.IsNullOrEmpty(splatFolder))
        {
            Debug.LogWarning("[SplatCycler] No splatFolder set. Assign a folder path in the Inspector.");
            return;
        }

        ScanFolder();

        if (autoLoadFirst && _files.Count > 0)
            LoadIndex(0);
    }

    void Update()
    {
        if (loader != null && preloadUpcomingFiles && !_moviePlaying)
            loader.PumpCompletedPreloads(finalizePreloadsPerFrame);

        // Movie playback
        if (_moviePlaying && loader != null && loader.IsMovieReady)
        {
            _movieTimer += Time.deltaTime;
            float interval = 1f / Mathf.Max(1, movieFps);
            if (_movieTimer >= interval)
            {
                _movieTimer -= interval;
                _movieFrame = (_movieFrame + 1) % loader.MovieFrameCount;
                loader.ShowMovieFrame(_movieFrame);
                _currentIndex = _movieFrame;
                if (_movieFrame >= 0 && _movieFrame < _files.Count)
                    _currentFile = Path.GetFileName(_files[_movieFrame]);
            }
            return; // skip manual input during playback
        }

        if (_files.Count == 0) return;

        if (XRSettings.isDeviceActive)
            HandleVRInput();
        else
            HandleKeyboardInput();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public string CurrentFileName => _currentFile;
    public int CurrentIndex => _currentIndex;
    public int TotalFiles => _totalFiles;
    public IReadOnlyList<string> Files => _files;

    /// <summary>Set the current index without loading (used by VRFileBrowser after direct load).</summary>
    public void SetIndex(int index)
    {
        if (index >= 0 && index < _files.Count)
        {
            _currentIndex = index;
            _currentFile = Path.GetFileName(_files[index]);
            RefreshPreloadWindow();
        }
    }

    public void LoadNext()
    {
        if (_files.Count == 0) return;
        int next = (_currentIndex + 1) % _files.Count;
        LoadIndex(next);
    }

    public void LoadPrevious()
    {
        if (_files.Count == 0) return;
        int prev = (_currentIndex - 1 + _files.Count) % _files.Count;
        LoadIndex(prev);
    }

    public void ScanFolder()
    {
        _files.Clear();
        _currentIndex = -1;
        _currentFile = "(none)";

        if (!Directory.Exists(splatFolder))
        {
            Debug.LogError($"[SplatCycler] Folder not found: {splatFolder}");
            _totalFiles = 0;
            return;
        }

        _files = Directory.GetFiles(splatFolder)
            .Where(f =>
            {
                return RuntimeSplatLoader.IsSupportedFileExtension(f);
            })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _totalFiles = _files.Count;
        Debug.Log($"[SplatCycler] Found {_totalFiles} splat file(s) in: {splatFolder}");
    }

    public void LoadIndex(int index)
    {
        if (index < 0 || index >= _files.Count) return;

        string path = _files[index];
        Debug.Log($"[SplatCycler] Loading [{index + 1}/{_files.Count}]: {Path.GetFileName(path)}");

        if (loader.LoadFile(path))
        {
            _currentIndex = index;
            _currentFile = Path.GetFileName(path);
            RefreshPreloadWindow();
            if (_rig != null) _rig.ResetToSpawnPoint(loader != null ? loader.targetRenderer : null);
        }
    }

    public void ApplyPreloadBudget()
    {
        if (loader != null)
            loader.SetPreloadBudgetFraction(preloadUpcomingFiles ? preloadRamFraction : 0f);
    }

    // ── Movie Mode API ────────────────────────────────────────────────────────

    /// <summary>
    /// Check if all files in the current folder fit in RAM for movie mode.
    /// Returns (fits, estimatedMB, availableMB).
    /// </summary>
    public (bool fits, long estimatedMB, long availableMB) CheckMovieFit()
    {
        if (_files.Count == 0) return (false, 0, 0);
        var (fits, est, avail) = RuntimeSplatLoader.CheckMovieRamFit(_files);
        return (fits, est / (1024 * 1024), avail / (1024 * 1024));
    }

    /// <summary>Start loading all frames for movie mode. Returns false if RAM check fails.</summary>
    public bool BeginMovieLoad(Action<int, int> onProgress)
    {
        if (loader == null || _files.Count == 0) return false;

        var (fits, est, avail) = RuntimeSplatLoader.CheckMovieRamFit(_files);
        if (!fits)
        {
            Debug.LogError($"[SplatCycler] Movie mode: not enough RAM. Need ~{est / (1024 * 1024)}MB, available ~{avail / (1024 * 1024)}MB");
            return false;
        }

        // Stop preloading to free RAM and avoid I/O contention with movie decode
        loader.SetPreloadTargets(Array.Empty<string>());

        return loader.BeginMovieLoad(_files, onProgress);
    }

    /// <summary>Pump movie loading (call each frame during load). Returns true when done.</summary>
    public bool PumpMovieLoad()
    {
        return loader != null && loader.PumpMovieLoad();
    }

    /// <summary>Start movie playback (all frames must be loaded first).</summary>
    public void StartMoviePlayback()
    {
        if (loader == null || !loader.IsMovieReady) return;
        _moviePlaying = true;
        _movieFrame = Mathf.Max(0, _currentIndex);
        _movieTimer = 0f;
        loader.ShowMovieFrame(_movieFrame);
        Debug.Log($"[SplatCycler] Movie playback started at {movieFps} FPS ({loader.MovieFrameCount} frames)");
    }

    /// <summary>Stop movie playback and release frames.</summary>
    public void StopMovie()
    {
        _moviePlaying = false;
        _movieTimer = 0f;
        if (loader != null) loader.StopMovie();
        Debug.Log("[SplatCycler] Movie mode stopped");
    }

    public void AdjustMovieFps(int delta)
    {
        movieFps = Mathf.Clamp(movieFps + delta, 1, 60);
    }

    public void RefreshPreloadWindow()
    {
        if (loader == null)
            return;

        if (!preloadUpcomingFiles || _files.Count == 0)
        {
            loader.SetPreloadTargets(Array.Empty<string>());
            return;
        }

        // Build the desired set by alternating forward/backward from current,
        // stopping when we'd exceed the RAM budget.
        long budget = loader.PreloadBudgetBytes;
        long accumulated = 0;
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Always include current
        if (_currentIndex >= 0 && _currentIndex < _files.Count)
            desired.Add(_files[_currentIndex]);

        if (_currentIndex < 0)
        {
            // Before first load — just queue first few files within budget
            for (int i = 0; i < _files.Count; i++)
            {
                long cost = RuntimeSplatLoader.EstimateAssetBytes(_files[i]);
                if (budget > 0 && accumulated + cost > budget) break;
                accumulated += cost;
                desired.Add(_files[i]);
            }
        }
        else
        {
            // Alternate: +1, -1, +2, -2, +3, -3, ...
            int maxOffset = _files.Count / 2 + 1;
            for (int offset = 1; offset <= maxOffset && desired.Count < _files.Count; offset++)
            {
                // Forward
                int fwd = (_currentIndex + offset) % _files.Count;
                if (!desired.Contains(_files[fwd]))
                {
                    long cost = RuntimeSplatLoader.EstimateAssetBytes(_files[fwd]);
                    if (budget > 0 && accumulated + cost > budget) break;
                    accumulated += cost;
                    desired.Add(_files[fwd]);
                }

                // Backward
                int bwd = (_currentIndex - offset + _files.Count) % _files.Count;
                if (!desired.Contains(_files[bwd]))
                {
                    long cost = RuntimeSplatLoader.EstimateAssetBytes(_files[bwd]);
                    if (budget > 0 && accumulated + cost > budget) break;
                    accumulated += cost;
                    desired.Add(_files[bwd]);
                }
            }
        }

        loader.SetPreloadTargets(desired);
        foreach (string filePath in desired)
        {
            if (_currentIndex >= 0 && string.Equals(filePath, _files[_currentIndex], StringComparison.OrdinalIgnoreCase))
                continue;
            loader.BeginPreload(filePath);
        }
    }

    // ── Input Handling ────────────────────────────────────────────────────────

    void HandleVRInput()
    {
        // Don't consume A/B when file browser is open (or just closed this frame)
        if (_browser != null && (_browser.IsOpen || _browser.WasOpenThisFrame)) return;

        var devices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(XRNode.RightHand, devices);

        bool bPressed = false;
        bool aPressed = false;

        if (devices.Count > 0)
        {
            devices[0].TryGetFeatureValue(CommonUsages.secondaryButton, out bPressed); // B
            devices[0].TryGetFeatureValue(CommonUsages.primaryButton, out aPressed);   // A
        }

        // B → next (with debounce)
        if (bPressed && _btnNextReady)
        {
            LoadNext();
            _btnNextReady = false;
        }
        else if (!bPressed)
        {
            _btnNextReady = true;
        }

        // A → previous (with debounce)
        if (aPressed && _btnPrevReady)
        {
            LoadPrevious();
            _btnPrevReady = false;
        }
        else if (!aPressed)
        {
            _btnPrevReady = true;
        }
    }

    void HandleKeyboardInput()
    {
        if (_browser != null && (_browser.IsOpen || _browser.WasOpenThisFrame))
            return;

        if (Input.GetKeyDown(KeyCode.R) || Input.GetKeyDown(KeyCode.PageDown) || Input.GetKeyDown(KeyCode.N))
            LoadNext();
        if (Input.GetKeyDown(KeyCode.F) || Input.GetKeyDown(KeyCode.PageUp) || Input.GetKeyDown(KeyCode.P))
            LoadPrevious();
    }
}
