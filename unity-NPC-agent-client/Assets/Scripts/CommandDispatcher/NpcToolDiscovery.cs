using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameWithLLM.AgentRuntime;

public static class AgentToolDiscovery
{
    private const string BuiltinToolsAssemblyName = "GameWithLLM.Client.BuiltinTools";

    public static void RegisterAll(ToolsRegistry registry)
    {
        if (registry == null)
            throw new ArgumentNullException(nameof(registry));

        Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate => string.Equals(
                candidate.GetName().Name,
                BuiltinToolsAssemblyName,
                StringComparison.Ordinal));
        if (assembly == null)
        {
            throw new InvalidOperationException(
                $"Builtin tool assembly '{BuiltinToolsAssemblyName}' is not loaded.");
        }

        foreach (IAgentTool tool in DiscoverFromAssembly(assembly))
            registry.RegisterTool(tool);
    }

    // 显式程序集发现是热更新工具包的唯一入口。任何已标记但不合法的工具类型
    // 都会令整个发现过程失败，调用方因此可以在 Registry 提交前拒绝整个包。
    public static IReadOnlyList<IAgentTool> DiscoverFromAssembly(Assembly assembly)
    {
        if (assembly == null)
            throw new ArgumentNullException(nameof(assembly));

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            string loaderErrors = string.Join(
                "; ",
                ex.LoaderExceptions
                    .Where(error => error != null)
                    .Select(error => error.Message));
            throw new InvalidOperationException(
                $"Unable to completely inspect tool assembly '{assembly.FullName}': {loaderErrors}",
                ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Unable to inspect tool assembly '{assembly.FullName}'.",
                ex);
        }

        var tools = new List<IAgentTool>();
        foreach (Type type in types
                     .Where(candidate => candidate != null &&
                         Attribute.IsDefined(candidate, typeof(AgentToolAttribute), false))
                     .OrderBy(candidate => candidate.FullName, StringComparer.Ordinal))
        {
            ValidateToolType(type, assembly);
            try
            {
                tools.Add((IAgentTool)Activator.CreateInstance(type));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Unable to instantiate tool type '{type.FullName}' from " +
                    $"assembly '{assembly.GetName().Name}'.",
                    ex);
            }
        }
        return tools;
    }

    private static void ValidateToolType(Type type, Assembly assembly)
    {
        if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters)
        {
            throw new InvalidOperationException(
                $"Marked tool type '{type.FullName}' in assembly " +
                $"'{assembly.GetName().Name}' must be a concrete, closed class.");
        }
        if (!typeof(IAgentTool).IsAssignableFrom(type))
        {
            throw new InvalidOperationException(
                $"Marked tool type '{type.FullName}' does not implement IAgentTool.");
        }
        if (type.GetConstructor(Type.EmptyTypes) == null)
        {
            throw new InvalidOperationException(
                $"Marked tool type '{type.FullName}' requires a public parameterless constructor.");
        }
    }
}
