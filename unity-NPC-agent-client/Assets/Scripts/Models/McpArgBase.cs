using System.Collections;
using UnityEngine;

/// <summary>
/// 所有 MCP 工具参数对象的基类，强制要求实现参数合法性检查
/// </summary>
public abstract class McpArgsBase
{
    // 子类必须实现这个方法，用来检查大模型传过来的参数是否符合游戏逻辑
    public abstract bool Validate(out string errorMessage);
}

/// <summary>
/// 统一的工具接口，让路由表(Dictionary)可以无视具体的泛型参数类型进行存储
/// </summary>
public interface IMcpTool
{
    string Execute(string argumentsJson);
}