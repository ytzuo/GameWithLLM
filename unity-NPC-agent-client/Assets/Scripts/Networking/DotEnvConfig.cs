using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

internal sealed class DotEnvConfig
{
    private readonly Dictionary<string, string> _values;
    private DotEnvConfig(Dictionary<string, string> values) { _values = values; }
    public static DotEnvConfig Load()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string root = FindRepoRoot();
        if (root != null)
        {
            LoadFile(Path.Combine(root, ".env"), values);
            LoadFile(Path.Combine(root, ".env.local"), values);
        }
        return new DotEnvConfig(values);
    }
    public string Get(string key, string fallback)
    {
        string environment = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(environment))
            return environment.Trim();
        return _values.TryGetValue(key, out string value) &&
               !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;
    }
    private static string FindRepoRoot()
    {
        foreach (string candidate in new[] { Directory.GetCurrentDirectory(), Application.dataPath })
        {
            var directory = new DirectoryInfo(candidate);
            while (directory != null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "GameMCPServer")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "unity-NPC-agent-client")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        return null;
    }
    private static void LoadFile(string path, Dictionary<string, string> values)
    {
        if (!File.Exists(path)) return;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            int separator = line.IndexOf('=');
            if (separator <= 0) continue;
            string key = line.Substring(0, separator).Trim();
            string value = line.Substring(separator + 1).Trim().Trim((char)34);
            if (key.Length > 0) values[key] = value;
        }
    }
}
