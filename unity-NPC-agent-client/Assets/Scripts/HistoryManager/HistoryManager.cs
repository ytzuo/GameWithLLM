using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

// HistoryManager 单例：对外暴露 LoadDialogueHistoryAsync / SaveDialogueHistoryAsync
public class HistoryManager : Singleton<HistoryManager>
{
    private IHistoryProvider _provider;

    protected override void Init()
    {
        if (_provider == null)
        {
            _provider = new FileHistoryProvider();
        }
    }

    // 运行时可以替换 provider（例如切换到数据库实现）
    public void SetProvider(IHistoryProvider provider)
    {
        _provider = provider ?? throw new System.ArgumentNullException(nameof(provider));
    }

    public Task<List<LlmMessage>> LoadDialogueHistoryAsync(string npcId)
    {
        return _provider.LoadHistoryAsync(npcId);
    }

    public Task SaveDialogueHistoryAsync(string npcId, List<LlmMessage> messages)
    {
        return _provider.SaveHistoryAsync(npcId, messages);
    }
}

