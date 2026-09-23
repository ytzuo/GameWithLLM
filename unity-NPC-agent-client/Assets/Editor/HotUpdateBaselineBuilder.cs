using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameWithLLM.AgentRuntime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class HotUpdateBaselineBuilder
{
    private const string BaselineRelativePath = "Docs/Baselines/builtin-tools.schema.json";

    [MenuItem("GameWithLLM/Hot Update/Capture Builtin Tool Schema Baseline")]
    public static void Capture()
    {
        string path = GetBaselinePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
        File.WriteAllText(path, BuildSnapshot() + Environment.NewLine);
        Debug.Log($"[Hot Update] Builtin tool schema baseline written to '{path}'.");
    }

    [MenuItem("GameWithLLM/Hot Update/Verify Builtin Tool Schema Baseline")]
    public static void Verify()
    {
        string path = GetBaselinePath();
        if (!File.Exists(path))
            throw new FileNotFoundException("Builtin tool schema baseline is missing.", path);

        string expected = Normalize(File.ReadAllText(path));
        string actual = Normalize(BuildSnapshot());
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Builtin tool schema differs from Docs/Baselines/builtin-tools.schema.json. " +
                "Review the contract change before deliberately capturing a new baseline.");

        VerifySampleSceneHasNoMissingScripts();
        Debug.Log("[Hot Update] Builtin tool schema baseline verified.");
    }

    public static void CaptureFromCommandLine()
    {
        try
        {
            Capture();
            EditorApplication.Exit(0);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorApplication.Exit(1);
        }
    }

    public static void VerifyFromCommandLine()
    {
        try
        {
            Verify();
            EditorApplication.Exit(0);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorApplication.Exit(1);
        }
    }

    private static string BuildSnapshot()
    {
        var tools = new List<JObject>();
        foreach (Type type in TypeCache.GetTypesDerivedFrom<IAgentTool>()
                     .Where(IsBuiltinToolType)
                     .OrderBy(value => value.FullName, StringComparer.Ordinal))
        {
            var tool = (IAgentTool)Activator.CreateInstance(type);
            AgentToolDescriptor descriptor = tool.Descriptor ??
                throw new InvalidOperationException($"Tool '{type.FullName}' has no descriptor.");
            tools.Add(new JObject
            {
                ["name"] = descriptor.Name,
                ["inputSchema"] = JObject.Parse(descriptor.InputSchemaJson)
            });
        }

        tools.Sort((left, right) => StringComparer.Ordinal.Compare(
            left.Value<string>("name"),
            right.Value<string>("name")));
        return new JObject
        {
            ["schemaVersion"] = 1,
            ["tools"] = new JArray(tools)
        }.ToString(Formatting.Indented);
    }

    private static bool IsBuiltinToolType(Type type)
    {
        if (type == null || type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
            return false;
        if (!Attribute.IsDefined(type, typeof(AgentToolAttribute), false))
            return false;

        string assemblyName = type.Assembly.GetName().Name;
        return string.Equals(assemblyName, "Assembly-CSharp", StringComparison.Ordinal) ||
               string.Equals(assemblyName, "GameWithLLM.Client.BuiltinTools", StringComparison.Ordinal);
    }

    private static string Normalize(string json) =>
        JToken.Parse(json).ToString(Formatting.None);

    private static void VerifySampleSceneHasNoMissingScripts()
    {
        const string scenePath = "Assets/Scenes/SampleScene.unity";
        var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        var missing = new List<string>();
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                int count = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(
                    transform.gameObject);
                if (count > 0)
                    missing.Add($"{GetHierarchyPath(transform)} ({count})");
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "SampleScene contains missing scripts: " + string.Join(", ", missing));
        }
        Debug.Log("[Hot Update] SampleScene contains no missing scripts.");
    }

    private static string GetHierarchyPath(Transform transform)
    {
        string path = transform.name;
        while (transform.parent != null)
        {
            transform = transform.parent;
            path = transform.name + "/" + path;
        }
        return path;
    }

    private static string GetBaselinePath()
    {
        DirectoryInfo projectDirectory = Directory.GetParent(Application.dataPath) ??
                                         throw new InvalidOperationException("Unity project path is unavailable.");
        DirectoryInfo repositoryDirectory = projectDirectory.Parent ??
                                            throw new InvalidOperationException("Repository path is unavailable.");
        return Path.Combine(repositoryDirectory.FullName, BaselineRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
