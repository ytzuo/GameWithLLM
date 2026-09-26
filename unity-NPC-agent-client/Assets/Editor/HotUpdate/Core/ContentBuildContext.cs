using System;
using System.IO;
using UnityEngine;

public sealed class ContentBuildContext
{
    public ContentBuildContext(string projectRoot = null, string repositoryRoot = null)
    {
        ProjectRoot = Path.GetFullPath(projectRoot ?? Path.Combine(Application.dataPath, ".."));
        RepositoryRoot = Path.GetFullPath(repositoryRoot ??
            Directory.GetParent(ProjectRoot)?.FullName ?? ProjectRoot);
    }

    public string ProjectRoot { get; }
    public string RepositoryRoot { get; }
    public string ArtifactsRoot => Path.Combine(RepositoryRoot, "Artifacts");

    public string ResolveProjectPath(params string[] segments) =>
        Path.GetFullPath(Path.Combine(Prepend(ProjectRoot, segments)));

    public string ResolveArtifactPath(params string[] segments) =>
        Path.GetFullPath(Path.Combine(Prepend(ArtifactsRoot, segments)));

    private static string[] Prepend(string root, string[] segments)
    {
        var values = new string[(segments?.Length ?? 0) + 1];
        values[0] = root;
        if (segments != null)
            Array.Copy(segments, 0, values, 1, segments.Length);
        return values;
    }
}
