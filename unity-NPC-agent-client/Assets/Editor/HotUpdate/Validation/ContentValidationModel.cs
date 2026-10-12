using System;
using System.Collections.Generic;
using Newtonsoft.Json;

public enum ContentValidationProfile
{
    Fast,
    Candidate,
    Release,
    Module
}

public sealed class ContentValidationContext
{
    public ContentValidationContext(ContentValidationProfile profile, string module = null,
        ContentBuildContext build = null)
    {
        Profile = profile;
        Module = module;
        Build = build ?? new ContentBuildContext();
    }

    public ContentValidationProfile Profile { get; }
    public string Module { get; }
    public ContentBuildContext Build { get; }
}

public interface IContentValidationRule
{
    string RuleId { get; }
    string Module { get; }
    ValidationResult Validate(ContentValidationContext context);
}

public sealed class ValidationResult
{
    [JsonProperty("module")]
    public string Module { get; set; }

    [JsonProperty("rule")]
    public string Rule { get; set; }

    [JsonProperty("succeeded")]
    public bool Succeeded { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; }

    public static ValidationResult Success(IContentValidationRule rule, string message = "Validation passed.") =>
        new ValidationResult
        {
            Module = rule.Module,
            Rule = rule.RuleId,
            Succeeded = true,
            Message = message
        };

    public static ValidationResult Failure(IContentValidationRule rule, Exception exception) =>
        new ValidationResult
        {
            Module = rule.Module,
            Rule = rule.RuleId,
            Succeeded = false,
            Message = exception?.Message ?? "Validation failed."
        };
}

public sealed class ContentValidationReport
{
    [JsonProperty("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonProperty("profile")]
    public string Profile { get; set; }

    [JsonProperty("succeeded")]
    public bool Succeeded { get; set; }

    [JsonProperty("modules")]
    public IReadOnlyList<ValidationResult> Modules { get; set; } = Array.Empty<ValidationResult>();
}
