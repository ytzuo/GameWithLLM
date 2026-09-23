using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GameWithLLM.AgentRuntime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public sealed class ToolParameterMetadata
{
    public string Description { get; }
    public bool OmitDescription { get; }

    internal ToolParameterMetadata(string description, bool omitDescription)
    {
        Description = description;
        OmitDescription = omitDescription;
    }
}

public sealed class ToolMetadata
{
    private readonly ReadOnlyDictionary<string, ToolParameterMetadata> _parameters;

    public string Description { get; }
    public string UsageHint { get; }
    public IReadOnlyDictionary<string, ToolParameterMetadata> Parameters => _parameters;

    internal ToolMetadata(
        string description,
        string usageHint,
        IDictionary<string, ToolParameterMetadata> parameters)
    {
        Description = description;
        UsageHint = usageHint;
        _parameters = new ReadOnlyDictionary<string, ToolParameterMetadata>(
            new Dictionary<string, ToolParameterMetadata>(parameters, StringComparer.Ordinal));
    }
}

// H5 文案快照。解析完成后不暴露可变 DTO，Registry 只交换完整快照引用。
public sealed class ToolMetadataCatalog
{
    private const int SupportedSchemaVersion = 1;
    private const int MaxTextLength = 2048;
    private readonly ReadOnlyDictionary<string, ToolMetadata> _tools;

    public int SchemaVersion { get; }
    public string ContentVersion { get; }
    public string Locale { get; }
    public string Fingerprint { get; }
    public bool IsIdentifierFallback { get; }
    public IReadOnlyDictionary<string, ToolMetadata> Tools => _tools;

    private ToolMetadataCatalog(
        int schemaVersion,
        string contentVersion,
        string locale,
        IDictionary<string, ToolMetadata> tools,
        string fingerprint,
        bool isIdentifierFallback = false)
    {
        SchemaVersion = schemaVersion;
        ContentVersion = contentVersion;
        Locale = locale;
        _tools = new ReadOnlyDictionary<string, ToolMetadata>(
            new Dictionary<string, ToolMetadata>(tools, StringComparer.Ordinal));
        Fingerprint = fingerprint;
        IsIdentifierFallback = isIdentifierFallback;
    }

    public static ToolMetadataCatalog ParseAndValidate(
        string json,
        ToolSetSnapshot toolSet,
        string expectedContentVersion = null,
        string expectedLocale = "zh-CN")
    {
        if (toolSet == null)
            throw new ArgumentNullException(nameof(toolSet));
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("Tool metadata Catalog JSON is empty.");

        ToolMetadataCatalogDto dto;
        JToken parsed;
        try
        {
            parsed = JToken.Parse(json, new JsonLoadSettings
            {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                CommentHandling = CommentHandling.Ignore,
                LineInfoHandling = LineInfoHandling.Ignore
            });
            if (!(parsed is JObject))
                throw new JsonSerializationException("Catalog root must be a JSON object.");
            dto = parsed.ToObject<ToolMetadataCatalogDto>(JsonSerializer.Create(
                new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error }));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Tool metadata Catalog JSON is invalid: {ex.Message}", ex);
        }
        if (dto == null || dto.schemaVersion != SupportedSchemaVersion)
            throw new InvalidOperationException($"Tool metadata Catalog schemaVersion must be {SupportedSchemaVersion}.");
        string version = ValidateIdentifier(dto.contentVersion, "contentVersion");
        string locale = ValidateIdentifier(dto.locale, "locale");
        if (!string.Equals(locale, expectedLocale, StringComparison.Ordinal))
            throw new InvalidOperationException($"Tool metadata Catalog locale must be '{expectedLocale}'.");
        if (!string.IsNullOrEmpty(expectedContentVersion) &&
            !string.Equals(version, expectedContentVersion, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Tool metadata Catalog version '{version}' does not match release version '{expectedContentVersion}'.");
        if (dto.tools == null)
            throw new InvalidOperationException("Tool metadata Catalog tools object is required.");

        var result = new Dictionary<string, ToolMetadata>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, ToolMetadataDto> pair in dto.tools)
        {
            if (!toolSet.Tools.ContainsKey(pair.Key))
                throw new InvalidOperationException($"Catalog contains unknown or retired tool '{pair.Key}'.");
            if (pair.Value == null)
                throw new InvalidOperationException($"Catalog tool '{pair.Key}' is null.");
            string description = ValidateText(pair.Value.description, $"{pair.Key}.description", true);
            string usageHint = ValidateText(pair.Value.usageHint, $"{pair.Key}.usageHint", false);
            var parameters = new Dictionary<string, ToolParameterMetadata>(StringComparer.Ordinal);
            JObject schema = JObject.Parse(toolSet.Descriptors[pair.Key].InputSchemaJson);
            var properties = schema["properties"] as JObject ?? new JObject();
            Dictionary<string, ToolParameterMetadataDto> source = pair.Value.parameters ??
                new Dictionary<string, ToolParameterMetadataDto>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, ToolParameterMetadataDto> parameter in source)
            {
                if (string.Equals(parameter.Key, "entityId", StringComparison.Ordinal))
                    throw new InvalidOperationException("Catalog cannot override the routing field 'entityId'.");
                if (properties.Property(parameter.Key, StringComparison.Ordinal) == null)
                    throw new InvalidOperationException(
                        $"Catalog contains unknown parameter '{pair.Key}.{parameter.Key}'.");
                if (parameter.Value == null)
                    throw new InvalidOperationException($"Catalog parameter '{pair.Key}.{parameter.Key}' is null.");
                bool omitted = parameter.Value.omitDescription;
                string parameterDescription = ValidateText(
                    parameter.Value.description,
                    $"{pair.Key}.{parameter.Key}.description",
                    !omitted);
                if (omitted && !string.IsNullOrEmpty(parameterDescription))
                    throw new InvalidOperationException(
                        $"Catalog parameter '{pair.Key}.{parameter.Key}' cannot have both description and omitDescription.");
                parameters.Add(parameter.Key, new ToolParameterMetadata(parameterDescription, omitted));
            }
            foreach (JProperty property in properties.Properties())
            {
                if (!parameters.ContainsKey(property.Name))
                    throw new InvalidOperationException(
                        $"Catalog is missing parameter metadata for '{pair.Key}.{property.Name}'.");
            }
            result.Add(pair.Key, new ToolMetadata(description, usageHint, parameters));
        }
        foreach (string toolName in toolSet.Tools.Keys)
        {
            if (!result.ContainsKey(toolName))
                throw new InvalidOperationException($"Catalog is missing active tool '{toolName}'.");
        }

        string canonical = Canonicalize(parsed).ToString(Formatting.None);
        return new ToolMetadataCatalog(
            dto.schemaVersion,
            version,
            locale,
            result,
            ComputeSha256(canonical));
    }

    // 无发布 JSON 时只用稳定标识符占位，不从工具程序集复制任何描述文案。
    internal static ToolMetadataCatalog CreateIdentifierFallbackCatalog(
        ToolSetSnapshot toolSet,
        string contentVersion)
    {
        var tools = new JObject();
        foreach (KeyValuePair<string, AgentToolDescriptor> pair in toolSet.Descriptors)
        {
            var parameters = new JObject();
            var schema = JObject.Parse(pair.Value.InputSchemaJson);
            foreach (JProperty property in (schema["properties"] as JObject ?? new JObject()).Properties())
                parameters[property.Name] = new JObject { ["omitDescription"] = true };
            tools[pair.Key] = new JObject
            {
                ["description"] = pair.Key,
                ["parameters"] = parameters
            };
        }
        var root = new JObject
        {
            ["schemaVersion"] = SupportedSchemaVersion,
            ["contentVersion"] = contentVersion,
            ["locale"] = "zh-CN",
            ["tools"] = tools
        };
        ToolMetadataCatalog parsed = ParseAndValidate(
            root.ToString(Formatting.None),
            toolSet,
            contentVersion);
        return new ToolMetadataCatalog(
            parsed.SchemaVersion,
            parsed.ContentVersion,
            parsed.Locale,
            parsed.Tools.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            parsed.Fingerprint,
            true);
    }

    public AgentToolDescriptor Apply(AgentToolDescriptor structuralDescriptor)
    {
        if (structuralDescriptor == null)
            throw new ArgumentNullException(nameof(structuralDescriptor));
        if (!_tools.TryGetValue(structuralDescriptor.Name, out ToolMetadata metadata))
            throw new InvalidOperationException($"Catalog does not describe tool '{structuralDescriptor.Name}'.");
        var schema = JObject.Parse(structuralDescriptor.InputSchemaJson);
        var properties = schema["properties"] as JObject ?? new JObject();
        foreach (KeyValuePair<string, ToolParameterMetadata> pair in metadata.Parameters)
        {
            if (!pair.Value.OmitDescription)
                ((JObject)properties[pair.Key])["description"] = pair.Value.Description;
        }
        return new AgentToolDescriptor(
            structuralDescriptor.Name,
            string.IsNullOrEmpty(metadata.UsageHint)
                ? metadata.Description
                : metadata.Description + "\n" + metadata.UsageHint,
            schema.ToString(Formatting.None),
            structuralDescriptor.Interruptible,
            structuralDescriptor.SuggestedTimeout);
    }

    private static string ValidateIdentifier(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) || value.Any(char.IsControl))
            throw new InvalidOperationException($"Catalog {field} must be a trimmed value of at most 128 characters.");
        return value;
    }

    private static string ValidateText(string value, string field, bool required)
    {
        if (string.IsNullOrEmpty(value))
        {
            if (required)
                throw new InvalidOperationException($"Catalog {field} is required.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxTextLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) || value.Any(char.IsControl))
            throw new InvalidOperationException($"Catalog {field} must be trimmed and at most {MaxTextLength} characters.");
        return value;
    }

    private static JToken Canonicalize(JToken token)
    {
        if (token is JObject obj)
        {
            var result = new JObject();
            foreach (JProperty property in obj.Properties().OrderBy(item => item.Name, StringComparer.Ordinal))
                result.Add(property.Name, Canonicalize(property.Value));
            return result;
        }
        if (token is JArray array)
            return new JArray(array.Select(Canonicalize));
        return token.DeepClone();
    }

    private static string ComputeSha256(string value)
    {
        using (SHA256 sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value)))
                .Replace("-", string.Empty).ToLowerInvariant();
    }

    private sealed class ToolMetadataCatalogDto
    {
        public int schemaVersion;
        public string contentVersion;
        public string locale;
        public Dictionary<string, ToolMetadataDto> tools;
    }

    private sealed class ToolMetadataDto
    {
        public string description;
        public string usageHint;
        public Dictionary<string, ToolParameterMetadataDto> parameters;
    }

    private sealed class ToolParameterMetadataDto
    {
        public string description;
        public bool omitDescription;
    }
}

public sealed class PreparedToolMetadataCatalog
{
    internal ToolMetadataCatalog Catalog { get; }
    internal string BaseToolSetFingerprint { get; }
    internal string BaseCatalogFingerprint { get; }

    internal PreparedToolMetadataCatalog(
        ToolMetadataCatalog catalog,
        string baseToolSetFingerprint,
        string baseCatalogFingerprint)
    {
        Catalog = catalog;
        BaseToolSetFingerprint = baseToolSetFingerprint;
        BaseCatalogFingerprint = baseCatalogFingerprint;
    }
}
