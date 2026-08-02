using System;
using System.Threading;
using System.Threading.Tasks;
using GameWithLLM.AgentRuntime;
using Newtonsoft.Json;
using UnityEngine;

public class GameToolWrapper<T> where T : ToolArgsBase
{
    private readonly Func<T, CancellationToken, ValueTask<AgentToolResult>> _coreLogic;

    public GameToolWrapper(Func<T, CancellationToken, ValueTask<AgentToolResult>> coreLogic)
    {
        _coreLogic = coreLogic ?? throw new ArgumentNullException(nameof(coreLogic));
    }

    public async ValueTask<AgentToolResult> ExecuteAsync(
        string argumentsJson,
        CancellationToken cancellationToken)
    {
        if (!ToolContract<T>.TryDeserialize(argumentsJson, out T parsedArgs, out string parseError))
            return AgentToolResult.Failure("INVALID_ARGUMENTS", parseError);

        if (!parsedArgs.Validate(out string validationError))
            return AgentToolResult.Failure("VALIDATION_FAILED", $"参数校验失败：{validationError}");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await _coreLogic(parsedArgs, cancellationToken);
        }
        catch (ToolExecutionException ex)
        {
            Debug.LogWarning($"[Game Tool] {typeof(T).Name} failed ({ex.ErrorCode}): {ex.Message}");
            return AgentToolResult.Failure(
                ex.ErrorCode,
                ex.Message,
                ex.Data?.ToString(Formatting.None));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Game Tool] {typeof(T).Name} execution failed: {ex}");
            return AgentToolResult.Failure(
                "GAME_LOGIC_ERROR",
                $"游戏逻辑执行失败：{ex.Message}");
        }
    }
}
