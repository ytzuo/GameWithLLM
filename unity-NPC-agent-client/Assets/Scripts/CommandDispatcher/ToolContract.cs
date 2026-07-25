using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
public sealed class ToolParameterAttribute : Attribute
{
    public bool Required { get; set; }
    public string Description { get; set; }
    public double Minimum { get; set; } = double.NaN;
    public double Maximum { get; set; } = double.NaN;
    public int MinLength { get; set; } = -1;
    public int MaxLength { get; set; } = -1;
    public string Pattern { get; set; }
    public string[] AllowedValues { get; set; }
    public int MinItems { get; set; } = -1;
    public int MaxItems { get; set; } = -1;
    public bool UniqueItems { get; set; }
    public int ItemMinLength { get; set; } = -1;
    public int ItemMaxLength { get; set; } = -1;
    public string ItemPattern { get; set; }
    public string[] ItemAllowedValues { get; set; }
}

public static class ToolContract<TArgs> where TArgs : ToolArgsBase
{
    private static readonly JObject CachedSchema = ToolContractSchema.Create(typeof(TArgs));

    public static JObject GetInputSchema()
    {
        return (JObject)CachedSchema.DeepClone();
    }

    public static bool TryDeserialize(string argumentsJson, out TArgs args, out string error)
    {
        args = null;
        JToken arguments;
        try
        {
            arguments = JToken.Parse(argumentsJson ?? string.Empty);
        }
        catch (Exception ex)
        {
            error = $"参数 JSON 格式不正确：{ex.Message}";
            return false;
        }

        if (!ToolContractValidator.TryValidate(arguments, CachedSchema, "$", out error))
            return false;

        try
        {
            var settings = new JsonSerializerSettings
            {
                Culture = CultureInfo.InvariantCulture,
                MissingMemberHandling = MissingMemberHandling.Error,
                ObjectCreationHandling = ObjectCreationHandling.Replace
            };
            settings.Converters.Add(new StringEnumConverter());
            args = arguments.ToObject<TArgs>(JsonSerializer.Create(settings));
            if (args == null)
                throw new JsonSerializationException("反序列化结果为空。");
            return true;
        }
        catch (Exception ex)
        {
            error = $"参数无法反序列化为 {typeof(TArgs).Name}：{ex.Message}";
            return false;
        }
    }
}

internal static class ToolContractSchema
{
    public static JObject Create(Type argsType)
    {
        if (argsType == null)
            throw new ArgumentNullException(nameof(argsType));
        if (!typeof(ToolArgsBase).IsAssignableFrom(argsType))
            throw new InvalidOperationException($"工具参数类型 '{argsType.FullName}' 必须继承 ToolArgsBase。");

        return CreateObjectSchema(argsType, new HashSet<Type>());
    }

    private static JObject CreateObjectSchema(Type objectType, HashSet<Type> visiting)
    {
        if (!visiting.Add(objectType))
            throw new InvalidOperationException($"工具参数类型包含循环引用：'{objectType.FullName}'。");

        try
        {
            var properties = new JObject();
            var required = new JArray();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (MemberInfo member in GetSerializableMembers(objectType)
                         .OrderBy(GetJsonName, StringComparer.Ordinal))
            {
                string name = GetJsonName(member);
                if (string.IsNullOrWhiteSpace(name))
                    throw new InvalidOperationException(
                        $"工具参数成员 '{objectType.FullName}.{member.Name}' 的 JSON 名称不能为空。");
                if (!names.Add(name))
                    throw new InvalidOperationException(
                        $"工具参数类型 '{objectType.FullName}' 包含重复 JSON 字段 '{name}'。");

                Type memberType = GetMemberType(member);
                ToolParameterAttribute parameter =
                    member.GetCustomAttribute<ToolParameterAttribute>(true) ??
                    new ToolParameterAttribute();
                JObject propertySchema = CreateValueSchema(memberType, visiting);
                ApplyParameterConstraints(propertySchema, parameter, name);
                properties.Add(name, propertySchema);
                if (parameter.Required)
                    required.Add(name);
            }

            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties
            };
            if (required.Count > 0)
                schema["required"] = required;
            schema["additionalProperties"] = false;
            return schema;
        }
        finally
        {
            visiting.Remove(objectType);
        }
    }

    private static JObject CreateValueSchema(Type valueType, HashSet<Type> visiting)
    {
        Type nullableType = Nullable.GetUnderlyingType(valueType);
        if (nullableType != null)
        {
            throw new InvalidOperationException(
                $"工具参数暂不支持 Nullable<T>：'{valueType.FullName}'。请使用可选字段和明确默认值。");
        }

        if (valueType == typeof(string))
            return new JObject { ["type"] = "string" };
        if (valueType == typeof(bool))
            return new JObject { ["type"] = "boolean" };
        if (IsInteger(valueType))
            return new JObject { ["type"] = "integer" };
        if (IsNumber(valueType))
            return new JObject { ["type"] = "number" };
        if (valueType.IsEnum)
        {
            return new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray(Enum.GetNames(valueType))
            };
        }

        if (TryGetItemType(valueType, out Type itemType))
        {
            return new JObject
            {
                ["type"] = "array",
                ["items"] = CreateValueSchema(itemType, visiting)
            };
        }

        if (valueType.IsClass)
            return CreateObjectSchema(valueType, visiting);

        throw new InvalidOperationException(
            $"工具参数类型不支持成员类型 '{valueType.FullName}'。");
    }

    private static void ApplyParameterConstraints(
        JObject schema,
        ToolParameterAttribute parameter,
        string fieldName)
    {
        string type = schema.Value<string>("type");
        if (!string.IsNullOrWhiteSpace(parameter.Description))
            schema["description"] = parameter.Description;

        if (!double.IsNaN(parameter.Minimum) || !double.IsNaN(parameter.Maximum))
        {
            RequireType(type, fieldName, "number", "integer");
            if (!double.IsNaN(parameter.Minimum))
                schema["minimum"] = parameter.Minimum;
            if (!double.IsNaN(parameter.Maximum))
                schema["maximum"] = parameter.Maximum;
        }
        if (parameter.MinLength >= 0 || parameter.MaxLength >= 0 ||
            !string.IsNullOrEmpty(parameter.Pattern) || parameter.AllowedValues != null)
        {
            RequireType(type, fieldName, "string");
            ApplyStringConstraints(
                schema,
                parameter.MinLength,
                parameter.MaxLength,
                parameter.Pattern,
                parameter.AllowedValues,
                fieldName);
        }
        if (parameter.MinItems >= 0 || parameter.MaxItems >= 0 ||
            parameter.UniqueItems || parameter.ItemMinLength >= 0 ||
            parameter.ItemMaxLength >= 0 || !string.IsNullOrEmpty(parameter.ItemPattern) ||
            parameter.ItemAllowedValues != null)
        {
            RequireType(type, fieldName, "array");
            if (parameter.MinItems >= 0)
                schema["minItems"] = parameter.MinItems;
            if (parameter.MaxItems >= 0)
                schema["maxItems"] = parameter.MaxItems;
            if (parameter.UniqueItems)
                schema["uniqueItems"] = true;

            var itemSchema = (JObject)schema["items"];
            if (parameter.ItemMinLength >= 0 || parameter.ItemMaxLength >= 0 ||
                !string.IsNullOrEmpty(parameter.ItemPattern) ||
                parameter.ItemAllowedValues != null)
            {
                RequireType(itemSchema.Value<string>("type"), $"{fieldName}[]", "string");
                ApplyStringConstraints(
                    itemSchema,
                    parameter.ItemMinLength,
                    parameter.ItemMaxLength,
                    parameter.ItemPattern,
                    parameter.ItemAllowedValues,
                    $"{fieldName}[]");
            }
        }
    }

    private static void ApplyStringConstraints(
        JObject schema,
        int minLength,
        int maxLength,
        string pattern,
        string[] allowedValues,
        string fieldName)
    {
        if (minLength >= 0)
            schema["minLength"] = minLength;
        if (maxLength >= 0)
            schema["maxLength"] = maxLength;
        if (!string.IsNullOrEmpty(pattern))
        {
            try
            {
                _ = new Regex(pattern, RegexOptions.CultureInvariant);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    $"工具参数 '{fieldName}' 的正则表达式无效：{ex.Message}", ex);
            }
            schema["pattern"] = pattern;
        }
        if (allowedValues != null)
        {
            if (allowedValues.Length == 0)
                throw new InvalidOperationException($"工具参数 '{fieldName}' 的允许值不能为空数组。");
            if (allowedValues.Any(string.IsNullOrEmpty))
                throw new InvalidOperationException($"工具参数 '{fieldName}' 的允许值不能包含空值。");
            schema["enum"] = new JArray(allowedValues);
        }
    }

    private static IEnumerable<MemberInfo> GetSerializableMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
        IEnumerable<MemberInfo> fields = type.GetFields(flags)
            .Where(field =>
                !field.IsStatic &&
                !field.IsNotSerialized &&
                field.GetCustomAttribute<JsonIgnoreAttribute>(true) == null)
            .Cast<MemberInfo>();
        IEnumerable<MemberInfo> properties = type.GetProperties(flags)
            .Where(property =>
                property.CanRead &&
                property.CanWrite &&
                property.GetIndexParameters().Length == 0 &&
                property.GetCustomAttribute<JsonIgnoreAttribute>(true) == null)
            .Cast<MemberInfo>();
        return fields.Concat(properties);
    }

    private static string GetJsonName(MemberInfo member)
    {
        JsonPropertyAttribute jsonProperty = member.GetCustomAttribute<JsonPropertyAttribute>(true);
        return string.IsNullOrWhiteSpace(jsonProperty?.PropertyName)
            ? member.Name
            : jsonProperty.PropertyName;
    }

    private static Type GetMemberType(MemberInfo member)
    {
        if (member is FieldInfo field)
            return field.FieldType;
        if (member is PropertyInfo property)
            return property.PropertyType;
        throw new InvalidOperationException($"不支持的工具参数成员：'{member.MemberType}'。");
    }

    private static bool TryGetItemType(Type type, out Type itemType)
    {
        if (type.IsArray)
        {
            itemType = type.GetElementType();
            return true;
        }
        if (type.IsGenericType)
        {
            Type definition = type.GetGenericTypeDefinition();
            if (definition == typeof(List<>) ||
                definition == typeof(IList<>) ||
                definition == typeof(IReadOnlyList<>))
            {
                itemType = type.GetGenericArguments()[0];
                return true;
            }
        }
        itemType = null;
        return false;
    }

    private static bool IsInteger(Type type)
    {
        return type == typeof(byte) || type == typeof(sbyte) ||
               type == typeof(short) || type == typeof(ushort) ||
               type == typeof(int) || type == typeof(uint) ||
               type == typeof(long) || type == typeof(ulong);
    }

    private static bool IsNumber(Type type)
    {
        return type == typeof(float) || type == typeof(double) || type == typeof(decimal);
    }

    private static void RequireType(string actual, string fieldName, params string[] expected)
    {
        if (!expected.Contains(actual, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"工具参数 '{fieldName}' 的约束不适用于 C# 类型对应的 JSON 类型 '{actual}'。");
        }
    }
}

internal static class ToolContractValidator
{
    public static bool TryValidate(JToken value, JObject schema, string path, out string error)
    {
        string type = schema.Value<string>("type");
        if (!MatchesType(value, type))
        {
            error = $"{path} 必须是 JSON {DescribeType(type)}。";
            return false;
        }

        if (schema["enum"] is JArray allowed &&
            !allowed.Any(candidate => JToken.DeepEquals(candidate, value)))
        {
            error = $"{path} 必须是允许值之一：{string.Join(", ", allowed.Select(item => item.ToString(Formatting.None)))}。";
            return false;
        }

        switch (type)
        {
            case "object":
                return ValidateObject((JObject)value, schema, path, out error);
            case "array":
                return ValidateArray((JArray)value, schema, path, out error);
            case "string":
                return ValidateString(value.Value<string>(), schema, path, out error);
            case "integer":
            case "number":
                return ValidateNumber(value.Value<double>(), schema, path, out error);
            default:
                error = null;
                return true;
        }
    }

    private static bool ValidateObject(
        JObject value,
        JObject schema,
        string path,
        out string error)
    {
        var properties = schema["properties"] as JObject ?? new JObject();
        if (schema["required"] is JArray required)
        {
            foreach (JToken requiredName in required)
            {
                string name = requiredName.Value<string>();
                if (!value.TryGetValue(name, StringComparison.Ordinal, out JToken propertyValue) ||
                    propertyValue.Type == JTokenType.Null)
                {
                    error = $"{path}.{name} 是必填参数。";
                    return false;
                }
            }
        }

        foreach (JProperty property in value.Properties())
        {
            if (!properties.TryGetValue(
                    property.Name,
                    StringComparison.Ordinal,
                    out JToken propertySchema))
            {
                if (schema.Value<bool?>("additionalProperties") == false)
                {
                    error = $"{path}.{property.Name} 是未知参数。";
                    return false;
                }
                continue;
            }
            if (!TryValidate(
                    property.Value,
                    (JObject)propertySchema,
                    $"{path}.{property.Name}",
                    out error))
                return false;
        }

        error = null;
        return true;
    }

    private static bool ValidateArray(
        JArray value,
        JObject schema,
        string path,
        out string error)
    {
        int? minItems = schema.Value<int?>("minItems");
        int? maxItems = schema.Value<int?>("maxItems");
        if (minItems.HasValue && value.Count < minItems.Value)
        {
            error = $"{path} 至少需要 {minItems.Value} 个元素。";
            return false;
        }
        if (maxItems.HasValue && value.Count > maxItems.Value)
        {
            error = $"{path} 最多允许 {maxItems.Value} 个元素。";
            return false;
        }
        if (schema.Value<bool?>("uniqueItems") == true)
        {
            for (int i = 0; i < value.Count; i++)
            {
                for (int j = i + 1; j < value.Count; j++)
                {
                    if (JToken.DeepEquals(value[i], value[j]))
                    {
                        error = $"{path} 不能包含重复元素。";
                        return false;
                    }
                }
            }
        }

        if (schema["items"] is JObject itemSchema)
        {
            for (int i = 0; i < value.Count; i++)
            {
                if (!TryValidate(value[i], itemSchema, $"{path}[{i}]", out error))
                    return false;
            }
        }
        error = null;
        return true;
    }

    private static bool ValidateString(
        string value,
        JObject schema,
        string path,
        out string error)
    {
        int? minLength = schema.Value<int?>("minLength");
        int? maxLength = schema.Value<int?>("maxLength");
        if (minLength.HasValue && value.Length < minLength.Value)
        {
            error = $"{path} 长度不能小于 {minLength.Value}。";
            return false;
        }
        if (maxLength.HasValue && value.Length > maxLength.Value)
        {
            error = $"{path} 长度不能大于 {maxLength.Value}。";
            return false;
        }
        string pattern = schema.Value<string>("pattern");
        if (!string.IsNullOrEmpty(pattern) &&
            !Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant))
        {
            error = $"{path} 格式不符合约束。";
            return false;
        }
        error = null;
        return true;
    }

    private static bool ValidateNumber(
        double value,
        JObject schema,
        string path,
        out string error)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            error = $"{path} 必须是有限数值。";
            return false;
        }
        double? minimum = schema.Value<double?>("minimum");
        double? maximum = schema.Value<double?>("maximum");
        if (minimum.HasValue && value < minimum.Value)
        {
            error = $"{path} 不能小于 {minimum.Value.ToString(CultureInfo.InvariantCulture)}。";
            return false;
        }
        if (maximum.HasValue && value > maximum.Value)
        {
            error = $"{path} 不能大于 {maximum.Value.ToString(CultureInfo.InvariantCulture)}。";
            return false;
        }
        error = null;
        return true;
    }

    private static bool MatchesType(JToken value, string type)
    {
        if (value == null || value.Type == JTokenType.Null)
            return false;
        switch (type)
        {
            case "object":
                return value.Type == JTokenType.Object;
            case "array":
                return value.Type == JTokenType.Array;
            case "string":
                return value.Type == JTokenType.String;
            case "boolean":
                return value.Type == JTokenType.Boolean;
            case "integer":
                if (value.Type == JTokenType.Integer)
                    return true;
                if (value.Type != JTokenType.Float)
                    return false;
                double number = value.Value<double>();
                return !double.IsNaN(number) &&
                       !double.IsInfinity(number) &&
                       Math.Truncate(number) == number;
            case "number":
                return value.Type == JTokenType.Integer || value.Type == JTokenType.Float;
            default:
                return false;
        }
    }

    private static string DescribeType(string type)
    {
        switch (type)
        {
            case "object": return "对象";
            case "array": return "数组";
            case "string": return "字符串";
            case "boolean": return "布尔值";
            case "integer": return "整数";
            case "number": return "数字";
            default: return $"类型 '{type}'";
        }
    }
}
