// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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

        InitializeDesktopCursorState();
    }

    static void ApplyWindowMode()
    {
        if (UnityEngine.XR.XRSettings.isDeviceActive)
        {
            // Minimize the desktop mirror window — no need to render on desktop in VR mode
            MinimizeDesktopWindow();
        }
        else
        {
            Screen.fullScreenMode = FullScreenMode.FullScreenWindow;
            Screen.fullScreen = true;
        }
    }

    static void MinimizeDesktopWindow()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        ShowWindow(GetActiveWindow(), SW_MINIMIZE);
#endif
    }

#if UNITY_STANDALONE_WIN
    const int SW_MINIMIZE = 6;

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern IntPtr GetActiveWindow();
#endif

    static void InitializeDesktopCursorState()
    {
        if (!UnityEngine.XR.XRSettings.isDeviceActive)
        {
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

        // Reset world to neutral before loading
        var worldGrab = FindAnyObjectByType<WorldGrabManipulator>();
        if (worldGrab != null)
            worldGrab.ResetWorld();

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

        var rig = FindAnyObjectByType<VRRig>();
        if (rig != null)
            rig.ResetToSpawnPoint(loader.targetRenderer);

        Debug.Log($"[ViewerStartup] Auto-loaded launch file: {filePath}");
    }
}