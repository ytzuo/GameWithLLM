using System.Collections.Generic;
using System.Threading.Tasks;

// 抽象历史提供者：支持异步加载与保存
public interface IHistoryProvider
{
    Task<List<LlmMessage>> LoadHistoryAsync(string npcId);
    Task SaveHistoryAsync(string npcId, List<LlmMessage> messages);
}

