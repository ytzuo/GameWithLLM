using System;
using UnityEngine;

public class GameToolWrapper<T> where T : ToolArgsBase
{
    private readonly Func<T, ToolExecutionResult> _coreLogic;

    public GameToolWrapper(Func<T, ToolExecutionResult> coreLogic)
    {
        _coreLogic = coreLogic ?? throw new ArgumentNullException(nameof(coreLogic));
    }

    public ToolExecutionResult Execute(string argumentsJson)
    {
        T parsedArgs;
        try
        {
            parsedArgs = JsonUtility.FromJson<T>(argumentsJson);
            if (parsedArgs == null)
                throw new InvalidOperationException("JSON 解析结果为空。");
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Failure("INVALID_ARGUMENTS", $"参数 JSON 格式不正确或解析失败：{ex.Message}");
        }

        if (!parsedArgs.Validate(out string validationError))
            return ToolExecutionResult.Failure("VALIDATION_FAILED", $"参数校验失败：{validationError}");

        try
        {
            return _coreLogic(parsedArgs);
        }
        catch (ToolExecutionException ex)
        {
            Debug.LogWarning($"[Game Tool] {typeof(T).Name} failed ({ex.ErrorCode}): {ex.Message}");
            return ToolExecutionResult.Failure(ex.ErrorCode, ex.Message, ex.Data);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Game Tool] {typeof(T).Name} execution failed: {ex}");
            return ToolExecutionResult.Failure("GAME_LOGIC_ERROR", $"游戏逻辑执行失败：{ex.Message}");
        }
    }
}