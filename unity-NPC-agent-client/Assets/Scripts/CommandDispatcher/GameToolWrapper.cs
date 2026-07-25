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
        if (!ToolContract<T>.TryDeserialize(argumentsJson, out T parsedArgs, out string parseError))
            return ToolExecutionResult.Failure("INVALID_ARGUMENTS", parseError);

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
