using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

[InitializeOnLoad]
public static class BuildSetup
{
    const string ScenePath = "Assets/GSTestScene.unity";
    const string WindowsReleaseVersion = "1.0";
    const string AndroidBuildVersion = "quest-test";

    static BuildSetup()
    {
        var scenes = EditorBuildSettings.scenes;

        foreach (var s in scenes)
        {
            if (s.path == ScenePath)
                return; // already added
        }

        var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(scenes);
        list.Add(new EditorBuildSettingsScene(ScenePath, true));
        EditorBuildSettings.scenes = list.ToArray();
        Debug.Log($"[BuildSetup] Added '{ScenePath}' to Build Settings scenes.");
    }

    [MenuItem("Tools/Splatviewer/Build Windows Release")]
    public static void BuildWindowsReleaseMenu()
    {
        BuildWindowsRelease();
    }

    [MenuItem("Tools/Splatviewer/Build Android APK")]
    public static void BuildAndroidApkMenu()
    {
        BuildAndroidApk();
    }

    public static void BuildWindowsRelease()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string outputFolder = Path.Combine(projectRoot, "Release", WindowsReleaseVersion);
        Directory.CreateDirectory(outputFolder);

        string exePath = Path.Combine(outputFolder, "SplatViewer_VR.exe");
        string[] scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
        if (scenes.Length == 0)
            scenes = new[] { ScenePath };

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = exePath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
            throw new BuildFailedException($"Windows release build failed: {report.summary.result}");

        CopyAssociationHelpers(projectRoot, outputFolder);
        Debug.Log($"[BuildSetup] Windows release build completed: {exePath}");
    }

    public static void BuildAndroidApk()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string outputFolder = Path.Combine(projectRoot, "Builds", "Android", AndroidBuildVersion);
        Directory.CreateDirectory(outputFolder);

        string apkPath = Path.Combine(outputFolder, "SplatViewer_VR_Quest.apk");
        string[] scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
        if (scenes.Length == 0)
            scenes = new[] { ScenePath };

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = apkPath,
            target = BuildTarget.Android,
            options = BuildOptions.None,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
            throw new BuildFailedException($"Android APK build failed: {report.summary.result}");

        Debug.Log($"[BuildSetup] Android APK build completed: {apkPath}");
    }

    static void CopyAssociationHelpers(string projectRoot, string outputFolder)
    {
        string repoRoot = Path.GetFullPath(Path.Combine(projectRoot, "..", ".."));
        string toolsFolder = Path.Combine(repoRoot, "tools");

        string[] helperFiles =
        {
            "Register-SplatviewerFileAssociations.ps1",
            "Register-SplatviewerFileAssociations.cmd",
        };

        foreach (string helperFile in helperFiles)
        {
            string sourcePath = Path.Combine(toolsFolder, helperFile);
            if (!File.Exists(sourcePath))
                continue;

            string destinationPath = Path.Combine(outputFolder, helperFile);
            File.Copy(sourcePath, destinationPath, true);
        }
    }
}
