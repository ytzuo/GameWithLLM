using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

// 基于文件的历史实现（选项 A）：每个 NPC 一个 JSON 文件，位于 Application.persistentDataPath/npc_history
public class FileHistoryProvider : IHistoryProvider
{
    private readonly string baseDir;

    public FileHistoryProvider()
    {
        baseDir = Path.Combine(Application.persistentDataPath, "npc_history");
    }

    public async Task<List<LlmMessage>> LoadHistoryAsync(string npcId)
    {
        try
        {
            if (!Directory.Exists(baseDir)) return new List<LlmMessage>();
            string path = Path.Combine(baseDir, $"{npcId}.json");
            if (!File.Exists(path)) return new List<LlmMessage>();
            string txt = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var list = JsonConvert.DeserializeObject<List<LlmMessage>>(txt);
            return list ?? new List<LlmMessage>();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[FileHistoryProvider] LoadHistoryAsync failed for {npcId}: {ex.Message}");
            return new List<LlmMessage>();
        }
    }

    public async Task SaveHistoryAsync(string npcId, List<LlmMessage> messages)
    {
        try
        {
            if (!Directory.Exists(baseDir)) Directory.CreateDirectory(baseDir);
            string path = Path.Combine(baseDir, $"{npcId}.json");
            string txt = JsonConvert.SerializeObject(messages, Formatting.Indented);
            await File.WriteAllTextAsync(path, txt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[FileHistoryProvider] SaveHistoryAsync failed for {npcId}: {ex.Message}");
        }
    }
}

