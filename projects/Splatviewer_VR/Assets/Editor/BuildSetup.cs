using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class BuildSetup
{
    const string ScenePath = "Assets/GSTestScene.unity";

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

    public static void BuildWindowsRelease()
    {
        EnsureLatestProjectContent();

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string outputFolder = Path.Combine(projectRoot, "Builds");
        RecreateDirectory(outputFolder);

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

    static void EnsureLatestProjectContent()
    {
        EditorSceneManager.SaveOpenScenes();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
    }

    static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, true);

        Directory.CreateDirectory(path);
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
