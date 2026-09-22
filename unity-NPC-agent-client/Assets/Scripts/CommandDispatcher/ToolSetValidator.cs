using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GameWithLLM.AgentRuntime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static class ToolSetCandidateFactory
{
    public static ToolSetCandidate CreateBuiltinDefault(IEnumerable<IAgentTool> tools)
    {
        IAgentTool[] discovered = (tools ?? throw new ArgumentNullException(nameof(tools))).ToArray();
        var candidates = new List<ToolCandidate>(discovered.Length);
        var history = new List<ToolHistoryDeclaration>(discovered.Length);
        foreach (IAgentTool tool in discovered)
        {
            AgentToolDescriptor descriptor = tool.Descriptor;
            var declaration = new ToolReleaseDeclaration
            {
                name = descriptor.Name,
                toolIdentity = descriptor.Name,
                source = "builtin",
                implementationVersion = "1.0.0",
                contractVersion = "1.0.0",
                assemblyName = tool.GetType().Assembly.GetName().Name,
                schemaHash = ToolSetValidator.ComputeSchemaHash(descriptor.InputSchemaJson)
            };
            candidates.Add(new ToolCandidate(declaration, tool));
            history.Add(ToHistory(declaration, "builtin-default"));
        }
        return new ToolSetCandidate(
            "builtin-default",
            "0.0.0",
            "embedded-1",
            candidates,
            Array.Empty<RetiredToolDeclaration>(),
            history);
    }

    public static ToolHistoryDeclaration ToHistory(
        ToolReleaseDeclaration declaration,
        string releaseId,
        string retiredInToolSetVersion = null)
    {
        return new ToolHistoryDeclaration
        {
            name = declaration.name,
            toolIdentity = declaration.toolIdentity,
            source = declaration.source,
            implementationVersion = declaration.implementationVersion,
            contractVersion = declaration.contractVersion,
            schemaHash = declaration.schemaHash,
            assemblyHash = declaration.assemblyHash,
            firstReleaseId = releaseId,
            lastReleaseId = releaseId,
            retiredInToolSetVersion = retiredInToolSetVersion
        };
    }
}

public static class ToolSetValidator
{
    public static ToolSetSnapshot Prepare(ToolSetCandidate candidate, ToolSetSnapshot current)
    {
        if (candidate == null)
            throw new ArgumentNullException(nameof(candidate));
        string releaseId = ValidateText(candidate.ReleaseId, "releaseId");
        string toolSetVersion = ValidateVersion(candidate.ToolSetVersion, "toolSetVersion");
        string catalogVersion = ValidateText(candidate.CatalogVersion, "catalogVersion");

        var tools = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);
        var descriptors = new Dictionary<string, AgentToolDescriptor>(StringComparer.Ordinal);
        var declarations = new Dictionary<string, ToolReleaseDeclaration>(StringComparer.Ordinal);
        foreach (ToolCandidate entry in candidate.ActiveTools)
        {
            if (entry == null)
                throw new InvalidOperationException("Active tool candidate cannot be null.");
            ToolReleaseDeclaration declaration = entry.Declaration;
            AgentToolDescriptor descriptor = ValidateTool(entry.Tool);
            ValidateToolName(declaration.name);
            if (!string.Equals(descriptor.Name, declaration.name, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Tool declaration '{declaration.name}' resolved descriptor '{descriptor.Name}'.");
            if (!tools.TryAdd(declaration.name, entry.Tool))
                throw new InvalidOperationException($"Duplicate active tool '{declaration.name}'.");

            ValidateText(declaration.toolIdentity, $"{declaration.name}.toolIdentity");
            ValidateVersion(declaration.implementationVersion, $"{declaration.name}.implementationVersion");
            ValidateVersion(declaration.contractVersion, $"{declaration.name}.contractVersion");
            string actualAssembly = entry.Tool.GetType().Assembly.GetName().Name;
            if (!string.Equals(actualAssembly, declaration.assemblyName, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Tool '{declaration.name}' resolved from assembly '{actualAssembly}', not '{declaration.assemblyName}'.");

            bool builtin = string.Equals(declaration.source, "builtin", StringComparison.Ordinal);
            bool hotUpdate = string.Equals(declaration.source, "hot-update", StringComparison.Ordinal);
            if (!builtin && !hotUpdate)
                throw new InvalidOperationException($"Tool '{declaration.name}' has invalid source '{declaration.source}'.");
            if (builtin)
            {
                if (!string.Equals(actualAssembly, "GameWithLLM.Client.BuiltinTools", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Builtin tool '{declaration.name}' is not from the builtin assembly.");
                if (!string.IsNullOrEmpty(declaration.packageId) ||
                    !string.IsNullOrEmpty(declaration.packageVersion) ||
                    !string.IsNullOrEmpty(declaration.assemblyHash))
                    throw new InvalidOperationException($"Builtin tool '{declaration.name}' cannot declare package fields.");
            }
            else
            {
                ValidateText(declaration.packageId, $"{declaration.name}.packageId");
                ValidateVersion(declaration.packageVersion, $"{declaration.name}.packageVersion");
                ValidateHash(declaration.assemblyHash, $"{declaration.name}.assemblyHash");
            }

            string schemaHash = ComputeSchemaHash(descriptor.InputSchemaJson);
            ValidateHash(declaration.schemaHash, $"{declaration.name}.schemaHash");
            if (!string.Equals(schemaHash, declaration.schemaHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Tool '{declaration.name}' structural Schema hash does not match its declaration.");
            declarations.Add(declaration.name, declaration);
            descriptors.Add(declaration.name, descriptor);
        }
        if (tools.Count == 0)
            throw new InvalidOperationException("A ToolSet must contain at least one active tool.");

        var retired = new Dictionary<string, RetiredToolDeclaration>(StringComparer.Ordinal);
        foreach (RetiredToolDeclaration declaration in candidate.RetiredTools)
        {
            if (declaration == null)
                throw new InvalidOperationException("Retired tool declaration cannot be null.");
            ValidateToolName(declaration.name);
            ValidateText(declaration.toolIdentity, $"{declaration.name}.toolIdentity");
            ValidateVersion(declaration.implementationVersion, $"{declaration.name}.implementationVersion");
            ValidateVersion(declaration.contractVersion, $"{declaration.name}.contractVersion");
            ValidateVersion(declaration.retiredInToolSetVersion, $"{declaration.name}.retiredInToolSetVersion");
            if (CompareVersions(declaration.retiredInToolSetVersion, toolSetVersion) > 0)
                throw new InvalidOperationException($"Retired tool '{declaration.name}' targets a future ToolSet.");
            if (tools.ContainsKey(declaration.name) || !retired.TryAdd(declaration.name, declaration))
                throw new InvalidOperationException($"Tool '{declaration.name}' cannot be both active and retired or repeated.");
        }

        var history = new Dictionary<string, ToolHistoryDeclaration>(StringComparer.Ordinal);
        foreach (ToolHistoryDeclaration record in candidate.History)
        {
            if (record == null)
                throw new InvalidOperationException("Tool history record cannot be null.");
            ValidateToolName(record.name);
            if (!history.TryAdd(record.name, record))
                throw new InvalidOperationException($"Duplicate history record for '{record.name}'.");
        }
        ValidateHistory(releaseId, declarations, retired, history);
        ValidateAgainstCurrent(current, declarations, retired);
        ValidateHistoryContinuity(current, history);

        string fingerprint = ComputeFingerprint(
            releaseId,
            toolSetVersion,
            catalogVersion,
            declarations.Values,
            retired.Values);
        if (current != null &&
            !string.Equals(current.Fingerprint, fingerprint, StringComparison.Ordinal) &&
            CompareVersions(toolSetVersion, current.ToolSetVersion) <= 0)
            throw new InvalidOperationException("A changed ToolSet must increase toolSetVersion.");
        return new ToolSetSnapshot(
            releaseId,
            toolSetVersion,
            catalogVersion,
            fingerprint,
            tools,
            descriptors,
            declarations,
            history,
            retired.Keys);
    }

    public static AgentToolDescriptor ValidateTool(IAgentTool tool)
    {
        if (tool == null)
            throw new ArgumentNullException(nameof(tool));
        AgentToolDescriptor descriptor;
        try { descriptor = tool.Descriptor; }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Tool type '{tool.GetType().FullName}' failed to create its Descriptor.", ex);
        }
        if (descriptor == null)
            throw new InvalidOperationException($"Tool type '{tool.GetType().FullName}' has no Descriptor.");
        ValidateToolName(descriptor.Name);
        if (string.IsNullOrWhiteSpace(descriptor.InputSchemaJson))
            throw new InvalidOperationException($"Tool '{descriptor.Name}' has an empty inputSchema.");
        try
        {
            if (!(JToken.Parse(descriptor.InputSchemaJson) is JObject schema))
                throw new InvalidOperationException("inputSchema must be a JSON object.");
            if (schema.DescendantsAndSelf().OfType<JObject>().Any(item => item["description"] != null))
                throw new InvalidOperationException(
                    $"Tool '{descriptor.Name}' structural inputSchema cannot contain descriptions.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Tool '{descriptor.Name}' has an invalid inputSchema: {ex.Message}", ex);
        }
        return descriptor;
    }

    public static string ComputeSchemaHash(string schemaJson)
    {
        JToken token = JToken.Parse(schemaJson);
        RemoveDescriptions(token);
        string canonical = Canonicalize(token).ToString(Formatting.None);
        return ComputeSha256(canonical);
    }

    private static void ValidateHistory(
        string releaseId,
        IReadOnlyDictionary<string, ToolReleaseDeclaration> active,
        IReadOnlyDictionary<string, RetiredToolDeclaration> retired,
        IReadOnlyDictionary<string, ToolHistoryDeclaration> history)
    {
        foreach (ToolReleaseDeclaration declaration in active.Values)
        {
            if (!history.TryGetValue(declaration.name, out ToolHistoryDeclaration record))
                throw new InvalidOperationException($"Active tool '{declaration.name}' is missing from the history ledger.");
            ValidateText(record.firstReleaseId, $"{record.name}.firstReleaseId");
            ValidateText(record.lastReleaseId, $"{record.name}.lastReleaseId");
            if (!string.Equals(record.toolIdentity, declaration.toolIdentity, StringComparison.Ordinal) ||
                !string.Equals(record.source, declaration.source, StringComparison.Ordinal) ||
                !string.Equals(record.implementationVersion, declaration.implementationVersion, StringComparison.Ordinal) ||
                !string.Equals(record.contractVersion, declaration.contractVersion, StringComparison.Ordinal) ||
                !string.Equals(record.schemaHash, declaration.schemaHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(record.assemblyHash ?? string.Empty, declaration.assemblyHash ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(record.retiredInToolSetVersion))
                throw new InvalidOperationException($"History ledger does not match active tool '{declaration.name}'.");
        }
        foreach (RetiredToolDeclaration declaration in retired.Values)
        {
            if (!history.TryGetValue(declaration.name, out ToolHistoryDeclaration record) ||
                !string.Equals(record.toolIdentity, declaration.toolIdentity, StringComparison.Ordinal) ||
                !string.Equals(record.implementationVersion, declaration.implementationVersion, StringComparison.Ordinal) ||
                !string.Equals(record.contractVersion, declaration.contractVersion, StringComparison.Ordinal) ||
                !string.Equals(record.retiredInToolSetVersion, declaration.retiredInToolSetVersion, StringComparison.Ordinal))
                throw new InvalidOperationException($"History ledger does not match retired tool '{declaration.name}'.");
        }
        foreach (ToolHistoryDeclaration record in history.Values)
        {
            if (!active.ContainsKey(record.name) && !retired.ContainsKey(record.name))
                throw new InvalidOperationException($"History tool '{record.name}' is neither active nor retired in release '{releaseId}'.");
        }
    }

    private static void ValidateAgainstCurrent(
        ToolSetSnapshot current,
        IReadOnlyDictionary<string, ToolReleaseDeclaration> next,
        IReadOnlyDictionary<string, RetiredToolDeclaration> retired)
    {
        if (current == null)
            return;
        foreach (KeyValuePair<string, ToolReleaseDeclaration> pair in current.Declarations)
        {
            if (!next.TryGetValue(pair.Key, out ToolReleaseDeclaration candidate))
            {
                if (!retired.TryGetValue(pair.Key, out RetiredToolDeclaration tombstone) ||
                    !string.Equals(tombstone.toolIdentity, pair.Value.toolIdentity, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Removed tool '{pair.Key}' requires a matching tombstone.");
                continue;
            }
            ToolReleaseDeclaration previous = pair.Value;
            if (!string.Equals(previous.toolIdentity, candidate.toolIdentity, StringComparison.Ordinal) ||
                !string.Equals(previous.source, candidate.source, StringComparison.Ordinal))
                throw new InvalidOperationException($"Published tool name '{pair.Key}' cannot change logical identity or source.");
            int implementationOrder = CompareVersions(candidate.implementationVersion, previous.implementationVersion);
            int contractOrder = CompareVersions(candidate.contractVersion, previous.contractVersion);
            if (implementationOrder < 0 || contractOrder < 0)
                throw new InvalidOperationException($"Tool '{pair.Key}' versions cannot go backwards.");
            bool implementationChanged =
                !string.Equals(previous.assemblyHash ?? string.Empty, candidate.assemblyHash ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(previous.packageVersion ?? string.Empty, candidate.packageVersion ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(previous.packageId ?? string.Empty, candidate.packageId ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(previous.assemblyName, candidate.assemblyName, StringComparison.Ordinal);
            bool schemaChanged = !string.Equals(previous.schemaHash, candidate.schemaHash, StringComparison.OrdinalIgnoreCase);
            if (implementationChanged && implementationOrder <= 0)
                throw new InvalidOperationException($"Tool '{pair.Key}' implementation changed without increasing implementationVersion.");
            if (schemaChanged && contractOrder <= 0)
                throw new InvalidOperationException($"Tool '{pair.Key}' Schema changed without increasing contractVersion.");
        }
        foreach (ToolReleaseDeclaration candidate in next.Values)
        {
            if (!current.IsRetired(candidate.name))
                continue;
            if (!current.History.TryGetValue(candidate.name, out ToolHistoryDeclaration old) ||
                !string.Equals(old.toolIdentity, candidate.toolIdentity, StringComparison.Ordinal) ||
                CompareVersions(candidate.implementationVersion, old.implementationVersion) <= 0)
                throw new InvalidOperationException($"Retired tool '{candidate.name}' can only be re-enabled with the same identity and a higher implementationVersion.");
        }
        foreach (ToolHistoryDeclaration previous in current.History.Values)
        {
            if (!current.IsRetired(previous.name) || next.ContainsKey(previous.name))
                continue;
            if (!retired.TryGetValue(previous.name, out RetiredToolDeclaration tombstone) ||
                !string.Equals(previous.toolIdentity, tombstone.toolIdentity, StringComparison.Ordinal) ||
                CompareVersions(tombstone.implementationVersion, previous.implementationVersion) != 0 ||
                CompareVersions(tombstone.contractVersion, previous.contractVersion) != 0)
                throw new InvalidOperationException($"Retired tool '{previous.name}' tombstone must be preserved by later ToolSets.");
        }
    }

    private static void ValidateHistoryContinuity(
        ToolSetSnapshot current,
        IReadOnlyDictionary<string, ToolHistoryDeclaration> nextHistory)
    {
        if (current == null || string.Equals(current.ReleaseId, "builtin-default", StringComparison.Ordinal))
            return;
        foreach (ToolHistoryDeclaration previous in current.History.Values)
        {
            if (!nextHistory.TryGetValue(previous.name, out ToolHistoryDeclaration next))
                throw new InvalidOperationException($"History record '{previous.name}' cannot be discarded.");
            if (!string.Equals(previous.toolIdentity, next.toolIdentity, StringComparison.Ordinal) ||
                !string.Equals(previous.source, next.source, StringComparison.Ordinal) ||
                !string.Equals(previous.firstReleaseId, next.firstReleaseId, StringComparison.Ordinal))
                throw new InvalidOperationException($"History identity for '{previous.name}' cannot be rewritten.");
        }
    }

    private static string ComputeFingerprint(
        string releaseId,
        string toolSetVersion,
        string catalogVersion,
        IEnumerable<ToolReleaseDeclaration> active,
        IEnumerable<RetiredToolDeclaration> retired)
    {
        var value = new StringBuilder()
            .Append(releaseId).Append('\n')
            .Append(toolSetVersion).Append('\n');
        foreach (ToolReleaseDeclaration item in active.OrderBy(item => item.name, StringComparer.Ordinal))
            value.Append(item.name).Append('|').Append(item.toolIdentity).Append('|').Append(item.source).Append('|')
                .Append(item.implementationVersion).Append('|').Append(item.contractVersion).Append('|')
                .Append(item.packageId).Append('|').Append(item.packageVersion).Append('|')
                .Append(item.assemblyName).Append('|').Append(item.assemblyHash).Append('|')
                .Append(item.schemaHash).Append('\n');
        foreach (RetiredToolDeclaration item in retired.OrderBy(item => item.name, StringComparer.Ordinal))
            value.Append("retired|").Append(item.name).Append('|').Append(item.toolIdentity).Append('|')
                .Append(item.implementationVersion).Append('|').Append(item.contractVersion).Append('|')
                .Append(item.retiredInToolSetVersion).Append('\n');
        return ComputeSha256(value.ToString());
    }

    private static JToken Canonicalize(JToken token)
    {
        if (token is JObject obj)
        {
            var result = new JObject();
            foreach (JProperty property in obj.Properties().OrderBy(property => property.Name, StringComparer.Ordinal))
                result.Add(property.Name, Canonicalize(property.Value));
            return result;
        }
        if (token is JArray array)
            return new JArray(array.Select(Canonicalize));
        return token.DeepClone();
    }

    private static void RemoveDescriptions(JToken token)
    {
        if (token is JObject obj)
        {
            obj.Remove("description");
            foreach (JProperty property in obj.Properties().ToArray())
                RemoveDescriptions(property.Value);
        }
        else if (token is JArray array)
        {
            foreach (JToken item in array)
                RemoveDescriptions(item);
        }
    }

    private static void ValidateToolName(string value)
    {
        ValidateText(value, "toolName");
        if (value.Length > 128 || !char.IsLower(value[0]) ||
            value.Any(character => !(character >= 'a' && character <= 'z') &&
                                   !(character >= '0' && character <= '9') &&
                                   character != '_'))
            throw new InvalidOperationException($"Tool name '{value}' must use lowercase ASCII letters, digits, and underscores.");
    }

    private static string ValidateText(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
            throw new InvalidOperationException($"{field} must be a trimmed value of at most 128 characters.");
        return value;
    }

    private static string ValidateVersion(string value, string field)
    {
        ValidateText(value, field);
        ParseVersion(value, field);
        return value;
    }

    private static int CompareVersions(string left, string right)
    {
        int[] a = ParseVersion(left, nameof(left));
        int[] b = ParseVersion(right, nameof(right));
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int av = i < a.Length ? a[i] : 0;
            int bv = i < b.Length ? b[i] : 0;
            if (av != bv)
                return av.CompareTo(bv);
        }
        return 0;
    }

    private static int[] ParseVersion(string value, string field)
    {
        string[] segments = value.Split('.');
        if (segments.Length == 0 || segments.Length > 4)
            throw new InvalidOperationException($"{field} must be a numeric dotted version.");
        var parsed = new int[segments.Length];
        for (int i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[i]) || parsed[i] < 0)
                throw new InvalidOperationException($"{field} must be a numeric dotted version.");
        }
        return parsed;
    }

    private static void ValidateHash(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException($"{field} must be a SHA-256 hexadecimal value.");
    }

    private static string ComputeSha256(string value)
    {
        using (SHA256 sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value)))
                .Replace("-", string.Empty).ToLowerInvariant();
    }
}
