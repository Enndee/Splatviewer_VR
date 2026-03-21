// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;

[DefaultExecutionOrder(-1000)]
public sealed class ViewerStartup : MonoBehaviour
{
    const float StartupDelaySeconds = 0.5f;

    static ViewerStartup s_instance;
    string _pendingFilePath;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (s_instance != null)
            return;

        var go = new GameObject(nameof(ViewerStartup));
        DontDestroyOnLoad(go);
        s_instance = go.AddComponent<ViewerStartup>();
    }

    void Awake()
    {
        ApplyPerformanceDefaults();
        ApplyWindowMode();
        _pendingFilePath = FindLaunchFilePath();
    }

    IEnumerator Start()
    {
        // Give XR and scene objects a moment to initialize before deciding mode and autoloading.
        yield return new WaitForSecondsRealtime(StartupDelaySeconds);
        ApplyWindowMode();

        if (!string.IsNullOrEmpty(_pendingFilePath))
            TryAutoLoadLaunchFile(_pendingFilePath);
        else
            OpenBrowserOnDirectLaunch();

        InitializeDesktopCursorState();
    }

    static void ApplyWindowMode()
    {
        int width = Display.main != null ? Display.main.systemWidth : Screen.currentResolution.width;
        int height = Display.main != null ? Display.main.systemHeight : Screen.currentResolution.height;
        Screen.SetResolution(width, height, FullScreenMode.Windowed);
        Screen.fullScreen = false;
    }

    static void ApplyPerformanceDefaults()
    {
        // Uncap frame rate on desktop so the GPU isn't artificially throttled.
        // In VR the headset compositor controls vsync, so this is harmless.
        Application.targetFrameRate = -1;
        QualitySettings.vSyncCount = 0;

        // Disable physics simulation — pure viewer, no rigidbodies.
        Physics.simulationMode = SimulationMode.Script;
    }

    static void InitializeDesktopCursorState()
    {
        if (!UnityEngine.XR.XRSettings.isDeviceActive)
        {
            var browser = FindAnyObjectByType<VRFileBrowser>();
            if (browser != null && browser.IsOpen)
                return;

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    static string FindLaunchFilePath()
    {
        string[] args;
        try
        {
            args = Environment.GetCommandLineArgs();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ViewerStartup] Could not read command line: {ex.Message}");
            return null;
        }

        return args
            .Skip(1)
            .Select(arg => arg.Trim().Trim('"'))
            .FirstOrDefault(IsSupportedLaunchFile);
    }

    static bool IsSupportedLaunchFile(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && File.Exists(path)
            && RuntimeSplatLoader.IsSupportedFileExtension(path);
    }

    static void TryAutoLoadLaunchFile(string filePath)
    {
        var loader = FindAnyObjectByType<RuntimeSplatLoader>();
        if (loader == null)
        {
            Debug.LogWarning($"[ViewerStartup] RuntimeSplatLoader not found for launch file: {filePath}");
            return;
        }

        if (!loader.LoadFile(filePath))
            return;

        string folder = Path.GetDirectoryName(filePath);
        string fileName = Path.GetFileName(filePath);

        var cycler = FindAnyObjectByType<SplatCycler>();
        if (cycler != null && !string.IsNullOrEmpty(folder))
        {
            cycler.splatFolder = folder;
            cycler.ScanFolder();
            for (int index = 0; index < cycler.Files.Count; index++)
            {
                if (Path.GetFileName(cycler.Files[index]).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    cycler.SetIndex(index);
                    break;
                }
            }
        }

        var browser = FindAnyObjectByType<VRFileBrowser>();
        if (browser != null)
            browser.SetCurrentFile(filePath);

        var rig = FindAnyObjectByType<VRRig>();
        if (rig != null)
            rig.ResetToSpawnPoint(loader.targetRenderer);

        Debug.Log($"[ViewerStartup] Auto-loaded launch file: {filePath}");
    }

    static void OpenBrowserOnDirectLaunch()
    {
        var browser = FindAnyObjectByType<VRFileBrowser>();
        if (browser != null && !browser.IsOpen)
            browser.ToggleBrowser();
    }
}