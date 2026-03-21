// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

[DefaultExecutionOrder(-1000)]
public sealed class ViewerStartup : MonoBehaviour
{
    const float StartupDelaySeconds = 0.5f;
    const float QuestRenderScale = 0.80f;
    const float QuestFoveatedRenderingLevel = 0f;
    const int QuestTargetFrameRate = 72;
    const int MaxDisplayOverrideFrames = 12;
    const GaussianSplatRenderer.SortMode QuestSortMode = GaussianSplatRenderer.SortMode.EveryNthFrame;
    const int QuestSortNthFrame = 12;
    const float QuestSortPositionThreshold = 0.02f;
    const float QuestSortAngleThreshold = 3.0f;
    const int QuestSortMaxFramesWithoutUpdate = 18;
    const float QuestAlphaClipThreshold = 6.0f / 255.0f;
    const float QuestSplatEdgeSharpness = 1.6f;
    const bool QuestUseOpaqueSplatHack = false;

    static ViewerStartup s_instance;
    static readonly List<XRDisplaySubsystem> s_displays = new();
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
        ApplyMobilePerformanceOverrides();
        ApplyMobileSplatRendererOverrides();
        QuestSplatStorage.EnsureRootFolderExists();
        _pendingFilePath = FindLaunchFilePath();
    }

    IEnumerator Start()
    {
        // Give XR and scene objects a moment to initialize before deciding mode and autoloading.
        yield return new WaitForSecondsRealtime(StartupDelaySeconds);
        ApplyWindowMode();
        ApplyMobilePerformanceOverrides();
        ApplyMobileSplatRendererOverrides();
        yield return ApplyMobileDisplayOverrides();

        EnsureOptionsMenu();

        if (!string.IsNullOrEmpty(_pendingFilePath))
            TryAutoLoadLaunchFile(_pendingFilePath);

        InitializeDesktopCursorState();
    }

    static void EnsureOptionsMenu()
    {
        if (FindAnyObjectByType<VROptionsMenu>() != null)
            return;
        var go = new GameObject(nameof(VROptionsMenu));
        DontDestroyOnLoad(go);
        go.AddComponent<VROptionsMenu>();
    }

    static void ApplyWindowMode()
    {
        Screen.fullScreenMode = FullScreenMode.FullScreenWindow;
        Screen.fullScreen = true;
    }

    static void ApplyMobilePerformanceOverrides()
    {
        if (!Application.isMobilePlatform)
            return;

        Application.targetFrameRate = QuestTargetFrameRate;
        QualitySettings.vSyncCount = 0;

        float renderScale = VROptionsMenu.LoadSavedFloat(VROptionsMenu.PrefKeyRenderScale, QuestRenderScale);
        renderScale = Mathf.Clamp(renderScale, 0.5f, 1f);
        XRSettings.eyeTextureResolutionScale = renderScale;
    }

    static void ApplyMobileSplatRendererOverrides()
    {
        if (!Application.isMobilePlatform)
            return;

        GaussianSplatRenderer[] renderers = FindObjectsByType<GaussianSplatRenderer>(FindObjectsSortMode.None);
        int savedSHOrder = (int)VROptionsMenu.LoadSavedFloat(VROptionsMenu.PrefKeySHOrder, -1);
        int savedSortNth = (int)VROptionsMenu.LoadSavedFloat(VROptionsMenu.PrefKeySortNth, -1);
        foreach (GaussianSplatRenderer renderer in renderers)
        {
            renderer.m_SortMode = QuestSortMode;
            renderer.m_SortNthFrame = savedSortNth > 0 ? savedSortNth : QuestSortNthFrame;
            renderer.m_SortPositionThreshold = QuestSortPositionThreshold;
            renderer.m_SortAngleThreshold = QuestSortAngleThreshold;
            renderer.m_SortMaxFramesWithoutUpdate = QuestSortMaxFramesWithoutUpdate;
            renderer.m_AlphaClipThreshold = QuestAlphaClipThreshold;
            renderer.m_SplatEdgeSharpness = QuestSplatEdgeSharpness;
            renderer.m_UseOpaqueRenderHack = QuestUseOpaqueSplatHack;
            if (savedSHOrder >= 0)
                renderer.m_SHOrder = savedSHOrder;
        }

        if (renderers.Length > 0)
        {
            Debug.Log($"[ViewerStartup] Applied Quest splat overrides to {renderers.Length} renderer(s): sort={QuestSortMode}, nthFrame={QuestSortNthFrame}, posThreshold={QuestSortPositionThreshold:0.000}, angleThreshold={QuestSortAngleThreshold:0.0}, alphaClip={QuestAlphaClipThreshold:0.00}, edgeSharpness={QuestSplatEdgeSharpness:0.0}, opaqueHack={QuestUseOpaqueSplatHack}");
        }
    }

    static IEnumerator ApplyMobileDisplayOverrides()
    {
        if (!Application.isMobilePlatform)
            yield break;

        for (int frame = 0; frame < MaxDisplayOverrideFrames; frame++)
        {
            yield return null;

            s_displays.Clear();
            SubsystemManager.GetSubsystems(s_displays);

            XRDisplaySubsystem display = s_displays.FirstOrDefault(subsystem => subsystem != null && subsystem.running);
            if (display == null)
                continue;

            display.foveatedRenderingLevel = Mathf.Clamp01(QuestFoveatedRenderingLevel);
            display.foveatedRenderingFlags = XRDisplaySubsystem.FoveatedRenderingFlags.None;
            Debug.Log($"[ViewerStartup] XR display overrides applied. Fixed foveated rendering level: {display.foveatedRenderingLevel:0.00}");
            yield break;
        }

        Debug.LogWarning("[ViewerStartup] XR display subsystem was not ready for runtime foveation overrides.");
    }

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
        if (Application.isMobilePlatform)
            return FindFirstSplatInRoot();

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

    static string FindFirstSplatInRoot()
    {
        string root = QuestSplatStorage.GetRootFolder();
        if (!Directory.Exists(root))
            return null;

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(RuntimeSplatLoader.IsSupportedFileExtension)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
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

        var rig = FindAnyObjectByType<VRRig>();
        if (rig != null)
            rig.ResetToSpawnPoint(loader.targetRenderer);

        Debug.Log($"[ViewerStartup] Auto-loaded launch file: {filePath}");
    }
}