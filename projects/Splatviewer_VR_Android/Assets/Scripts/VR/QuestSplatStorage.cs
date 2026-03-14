// SPDX-License-Identifier: MIT
using System.IO;
using UnityEngine;

static class QuestSplatStorage
{
    const string SplatFolderName = "Splats";

    public static string GetBrowseRootFolder()
    {
        return GetRootFolder();
    }

    public static string GetRootFolder()
    {
        return Path.Combine(GetBaseFolder(), SplatFolderName);
    }

    public static void EnsureRootFolderExists()
    {
        Directory.CreateDirectory(GetRootFolder());
    }

    public static bool IsInsideRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string root = Normalize(GetRootFolder());
        string candidate = Normalize(path);
        return candidate.StartsWith(root, System.StringComparison.OrdinalIgnoreCase);
    }

    static string GetBaseFolder()
    {
        return Application.isMobilePlatform
            ? Application.persistentDataPath
            : Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    }

    static string GetFallbackFolder()
    {
        return GetBaseFolder();
    }

    static string Normalize(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
    }
}