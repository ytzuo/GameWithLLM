using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// 核心管道拦截器：负责 JSON 反序列化、合法性校验、异常处理
/// </summary>
/// <typeparam name="T">该工具需要的具体参数类，必须继承自 McpArgsBase</typeparam>
public class McpToolWrapper<T> : IMcpTool where T : McpArgsBase
{
    private readonly Func<T, string> _coreLogic; // 真正的核心游戏逻辑委托

    public McpToolWrapper(Func<T, string> coreLogic)
    {
        _coreLogic = coreLogic;
    }

    // 所有的工具请求都会经过这个统一的入口进行拦截和加工
    public string Execute(string argumentsJson)
    {
        // 拦截点 1: 解析 JSON 为具体的强类型对象
        T parsedArgs;
        try
        {
            parsedArgs = JsonUtility.FromJson<T>(argumentsJson);
            if (parsedArgs == null) throw new Exception("JSON解析结果为空。");
        }
        catch (Exception ex)
        {
            return $"[MCP Protocol Error] 参数JSON格式不正确或解析失败: {ex.Message}";
        }

        // 拦截点 2: 验证参数合法性 (调用各个参数类自定义的校验规则)
        if (!parsedArgs.Validate(out string validationError))
        {
            return $"[MCP Validation Error] 参数校验失败: {validationError}";
        }

        // 拦截点 3: 安全地调用真正的游戏逻辑，并捕获运行期崩溃
        try
        {
            return _coreLogic(parsedArgs);
        }
        catch (Exception ex)
        {
            return $"[Game Logic Crash] 游戏内部逻辑错误: {ex.Message}";
        }
    }
}