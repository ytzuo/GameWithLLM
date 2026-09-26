using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class ContentValidationRunner
{
    private sealed class RegisteredRule
    {
        public RegisteredRule(IContentValidationRule rule, ContentValidationProfile minimumProfile)
        {
            Rule = rule;
            MinimumProfile = minimumProfile;
        }

        public IContentValidationRule Rule { get; }
        public ContentValidationProfile MinimumProfile { get; }
    }

    private static readonly RegisteredRule[] Rules =
    {
        new RegisteredRule(new ProjectConfigurationValidator(), ContentValidationProfile.Fast),
        new RegisteredRule(new ContentOwnershipValidator(), ContentValidationProfile.Fast),
        new RegisteredRule(new ContentCatalogValidator(), ContentValidationProfile.Fast),
        new RegisteredRule(new UiContentValidator(), ContentValidationProfile.Fast),
        new RegisteredRule(new ItemContentValidator(), ContentValidationProfile.Fast),
        new RegisteredRule(new CharacterContentValidator(), ContentValidationProfile.Fast),
        new RegisteredRule(new SceneContentValidator(), ContentValidationProfile.Fast),
        new RegisteredRule(new ToolPackageValidator(), ContentValidationProfile.Candidate),
        new RegisteredRule(new ReleaseBudgetValidator(), ContentValidationProfile.Candidate)
    };

    [MenuItem("GameWithLLM/Content/Validate Project")]
    public static void ValidateProject()
    {
        ContentValidationReport report = Run(ContentValidationProfile.Fast);
        ThrowIfFailed(report);
    }

    [MenuItem("GameWithLLM/Content/Validate Selected Module")]
    public static void ValidateSelectedModule()
    {
        string module = Environment.GetEnvironmentVariable("CONTENT_VALIDATION_MODULE");
        if (string.IsNullOrWhiteSpace(module))
            throw new InvalidOperationException(
                "Set CONTENT_VALIDATION_MODULE to a registered module name before running module validation.");
        ContentValidationReport report = Run(ContentValidationProfile.Module, module);
        ThrowIfFailed(report);
    }

    public static ContentValidationReport Run(ContentValidationProfile profile, string module = null,
        string reportPath = null, IEnumerable<IContentValidationRule> rules = null)
    {
        var context = new ContentValidationContext(profile, module);
        IEnumerable<IContentValidationRule> selected = rules ?? SelectRules(profile, module);
        var results = new List<ValidationResult>();
        foreach (IContentValidationRule rule in selected)
            results.Add(RunReadOnly(rule, context));

        var report = new ContentValidationReport
        {
            Profile = profile.ToString().ToLowerInvariant(),
            Succeeded = results.All(result => result.Succeeded),
            Modules = results
        };
        WriteReport(report, reportPath ?? context.Build.ResolveArtifactPath(
            "Validation", "content-validation-report.json"));
        Debug.Log($"[Content] Validation profile '{report.Profile}' completed: " +
                  $"{results.Count(result => result.Succeeded)}/{results.Count} rules passed.");
        return report;
    }

    public static void ValidateFromCommandLine() => HotUpdateEditorCommand.Run(() =>
    {
        string rawProfile = HotUpdateEditorCommand.GetArgument("-contentValidationProfile") ?? "candidate";
        if (!Enum.TryParse(rawProfile, true, out ContentValidationProfile profile))
            throw new ArgumentException($"Unknown content validation profile '{rawProfile}'.");
        string module = HotUpdateEditorCommand.GetArgument("-contentValidationModule");
        string reportPath = HotUpdateEditorCommand.GetArgument("-contentValidationReport");
        ContentValidationReport report = Run(profile, module, reportPath);
        ThrowIfFailed(report);
    });

    public static void ThrowIfFailed(ContentValidationReport report)
    {
        if (report == null)
            throw new ArgumentNullException(nameof(report));
        if (report.Succeeded)
            return;
        string failures = string.Join("; ", report.Modules.Where(result => !result.Succeeded)
            .Select(result => $"{result.Module}/{result.Rule}: {result.Message}"));
        throw new InvalidDataException("Content validation failed: " + failures);
    }

    private static IEnumerable<IContentValidationRule> SelectRules(ContentValidationProfile profile, string module)
    {
        if (profile == ContentValidationProfile.Module)
        {
            if (string.IsNullOrWhiteSpace(module))
                throw new ArgumentException("Module profile requires a module name.", nameof(module));
            IContentValidationRule[] matches = Rules.Select(entry => entry.Rule)
                .Where(rule => string.Equals(rule.Module, module, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length == 0)
                throw new ArgumentException($"Unknown content validation module '{module}'.", nameof(module));
            return matches;
        }

        return Rules.Where(entry => entry.MinimumProfile <= profile).Select(entry => entry.Rule);
    }

    private static ValidationResult RunReadOnly(IContentValidationRule rule, ContentValidationContext context)
    {
        SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
        ValidationResult result;
        try
        {
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                Scene scene = SceneManager.GetSceneAt(index);
                if (scene.isDirty)
                    throw new InvalidOperationException(
                        $"Save or revert dirty scene '{scene.path}' before running content validation.");
            }
            result = rule.Validate(context) ??
                     ValidationResult.Failure(rule, new InvalidOperationException("Rule returned no result."));
        }
        catch (Exception ex)
        {
            result = ValidationResult.Failure(rule, ex);
        }
        finally
        {
            try
            {
                if (setup.Length > 0)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(setup);
                }
                else if (Enumerable.Range(0, SceneManager.sceneCount)
                         .Select(SceneManager.GetSceneAt)
                         .Any(scene => !string.IsNullOrWhiteSpace(scene.path)))
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
            }
            catch (Exception ex)
            {
                result = ValidationResult.Failure(rule,
                    new InvalidOperationException("Failed to restore the Editor scene setup after validation.", ex));
            }
        }
        return result;
    }

    private static void WriteReport(ContentValidationReport report, string path)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ??
                                  throw new InvalidOperationException("Validation report path is invalid."));
        File.WriteAllText(fullPath,
            JsonConvert.SerializeObject(report, Formatting.Indented) + Environment.NewLine);
    }
}
