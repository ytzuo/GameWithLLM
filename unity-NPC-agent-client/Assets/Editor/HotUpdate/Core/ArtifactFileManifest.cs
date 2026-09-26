using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

public static class ArtifactFileManifest
{
    public static JArray Create(string repositoryRoot, IEnumerable<string> roots,
        IEnumerable<string> additionalFiles = null)
    {
        IEnumerable<string> files = (roots ?? Array.Empty<string>())
            .Where(Directory.Exists)
            .SelectMany(root => Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        if (additionalFiles != null)
            files = files.Concat(additionalFiles.Where(File.Exists));
        return new JArray(files
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => ArtifactHash.FileRecord(repositoryRoot, path)));
    }
}
