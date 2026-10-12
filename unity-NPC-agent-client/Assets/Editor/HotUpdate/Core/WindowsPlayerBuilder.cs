using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

public static class WindowsPlayerBuilder
{
    public static string Build(AddressableAssetSettings settings, string profileName,
        string outputPath, IEnumerable<string> scenes, BuildOptions options)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Player output path is required.", nameof(outputPath));
        string fullOutput = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput) ??
                                  throw new InvalidOperationException("Player output path is invalid."));
        BuildReport report;
        using (new AddressablesProfileScope(settings, profileName))
        {
            report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = (scenes ?? Array.Empty<string>()).ToArray(),
                locationPathName = fullOutput,
                target = BuildTarget.StandaloneWindows64,
                options = options
            });
        }
        if (report == null || report.summary.result != BuildResult.Succeeded)
            throw new BuildFailedException("Windows IL2CPP Player build failed: " +
                                           (report?.summary.result.ToString() ?? "no report") + ".");
        return fullOutput;
    }
}
