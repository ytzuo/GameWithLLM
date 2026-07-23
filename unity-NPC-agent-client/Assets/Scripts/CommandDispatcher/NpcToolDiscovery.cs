using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

public static class NpcToolDiscovery
{
    public static void RegisterAll(ToolsRegistry registry)
    {
        if (registry == null)
            throw new ArgumentNullException(nameof(registry));

        Type toolContract = typeof(INpcTool);
        IEnumerable<Type> toolTypes = AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(GetLoadableTypes)
            .Where(type =>
                type != null &&
                !type.IsAbstract &&
                !type.IsInterface &&
                !type.ContainsGenericParameters &&
                toolContract.IsAssignableFrom(type) &&
                Attribute.IsDefined(type, typeof(NpcToolAttribute), false))
            .OrderBy(type => type.FullName, StringComparer.Ordinal);

        foreach (Type toolType in toolTypes)
        {
            try
            {
                var tool = (INpcTool)Activator.CreateInstance(toolType);
                registry.RegisterTool(tool);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ToolsRegistry] 无法注册工具类型 '{toolType.FullName}': {ex}");
            }
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ToolsRegistry] 无法扫描程序集 '{assembly.FullName}': {ex.Message}");
            return Array.Empty<Type>();
        }
    }
}
