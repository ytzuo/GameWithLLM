using System;
using UnityEditor;
using UnityEngine;

public static class HotUpdateEditorCommand
{
    public static void Run(Action action)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));
        try
        {
            action();
            EditorApplication.Exit(0);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorApplication.Exit(1);
        }
    }

    public static string GetArgument(string name)
    {
        string[] arguments = Environment.GetCommandLineArgs();
        for (int index = 0; index < arguments.Length - 1; index++)
            if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                return arguments[index + 1];
        return null;
    }
}
