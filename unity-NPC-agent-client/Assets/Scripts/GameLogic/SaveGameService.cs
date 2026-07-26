using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

[Serializable]
public sealed class SaveGameFile
{
    [JsonProperty("version")] public int Version = 1;
    [JsonProperty("saveId")] public string SaveId;
    [JsonProperty("displayName")] public string DisplayName;
    [JsonProperty("savedAt")] public DateTime SavedAt;
    [JsonProperty("operationId")] public string OperationId;
    [JsonProperty("pendingConversationMode")] public string PendingConversationMode;
    [JsonProperty("conversationSynced")] public bool ConversationSynced;
    [JsonProperty("sceneName")] public string SceneName;
    [JsonProperty("entities")] public List<SaveGameEntityState> Entities = new List<SaveGameEntityState>();
    [JsonProperty("inventories")] public List<SaveGameInventoryState> Inventories = new List<SaveGameInventoryState>();
}

[Serializable]
public sealed class SaveGameEntityState
{
    [JsonProperty("entityId")] public string EntityId;
    [JsonProperty("position")] public SaveGameVector3 Position;
    [JsonProperty("rotation")] public SaveGameQuaternion Rotation;
}

[Serializable]
public sealed class SaveGameInventoryState
{
    [JsonProperty("containerId")] public string ContainerId;
    [JsonProperty("slots")] public List<SaveGameInventorySlotState> Slots = new List<SaveGameInventorySlotState>();
}

[Serializable]
public sealed class SaveGameInventorySlotState
{
    [JsonProperty("itemId")] public string ItemId;
    [JsonProperty("quantity")] public int Quantity;
}

[Serializable]
public struct SaveGameVector3
{
    [JsonProperty("x")] public float X;
    [JsonProperty("y")] public float Y;
    [JsonProperty("z")] public float Z;
    public SaveGameVector3(Vector3 value) { X = value.x; Y = value.y; Z = value.z; }
    public Vector3 ToUnity() => new Vector3(X, Y, Z);
}

[Serializable]
public struct SaveGameQuaternion
{
    [JsonProperty("x")] public float X;
    [JsonProperty("y")] public float Y;
    [JsonProperty("z")] public float Z;
    [JsonProperty("w")] public float W;
    public SaveGameQuaternion(Quaternion value) { X = value.x; Y = value.y; Z = value.z; W = value.w; }
    public Quaternion ToUnity() => new Quaternion(X, Y, Z, W);
}

public sealed class SaveGameSummary
{
    public string SaveId;
    public string DisplayName;
    public DateTime SavedAt;
    public bool ConversationSynced;
}

/// <summary>Unity 自有世界存档。Go 对话快照只通过同一 saveId 关联，不在这里落盘。</summary>
public sealed class SaveGameService
{
    private readonly PlayerMock _player;
    private readonly ItemDataList _itemCatalog;
    private readonly string _directory;

    public SaveGameService(PlayerMock player, ItemDataList itemCatalog)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _itemCatalog = itemCatalog;
        _directory = Path.Combine(Application.persistentDataPath, "SaveGames");
    }

    public IReadOnlyList<SaveGameSummary> ListSaves()
    {
        Directory.CreateDirectory(_directory);
        var result = new List<SaveGameSummary>();
        foreach (string path in Directory.GetFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                SaveGameFile file = ReadFile(path);
                ValidateIdentity(file);
                result.Add(new SaveGameSummary
                {
                    SaveId = file.SaveId,
                    DisplayName = file.DisplayName,
                    SavedAt = file.SavedAt,
                    ConversationSynced = file.ConversationSynced
                });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"忽略损坏的世界存档 '{Path.GetFileName(path)}': {ex.Message}");
            }
        }
        result.Sort((left, right) => right.SavedAt.CompareTo(left.SavedAt));
        return result;
    }

    public SaveGameFile Create(string displayName)
    {
        string saveId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        SaveGameFile file = Capture(saveId, NormalizeDisplayName(displayName), "create");
        WriteFile(file, false);
        return file;
    }

    public SaveGameFile Overwrite(string saveId)
    {
        SaveGameFile existing = Load(saveId);
        if (!existing.ConversationSynced)
            throw new InvalidOperationException("该存档的对话尚未同步，请先重试同步再覆盖。");
        SaveGameFile file = Capture(existing.SaveId, existing.DisplayName, "overwrite");
        WriteFile(file, true);
        return file;
    }

    public SaveGameFile Load(string saveId)
    {
        if (!IsCanonicalUuid(saveId))
            throw new InvalidDataException("saveId 不是 canonical UUID。");
        SaveGameFile file = ReadFile(GetPath(saveId));
        ValidateWorld(file);
        return file;
    }

    public void MarkConversationSynced(SaveGameFile file)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));
        file.ConversationSynced = true;
        WriteFile(file, true);
    }

    public void Apply(SaveGameFile file)
    {
        ValidatedWorld world = ValidateWorld(file);
        foreach (SaveGameEntityState state in file.Entities)
        {
            if (state.EntityId == _player.WorldTargetId)
            {
                CharacterController controller = _player.GetComponent<CharacterController>();
                bool wasEnabled = controller != null && controller.enabled;
                if (wasEnabled) controller.enabled = false;
                _player.transform.SetPositionAndRotation(state.Position.ToUnity(), state.Rotation.ToUnity());
                if (wasEnabled) controller.enabled = true;
            }
            else
            {
                world.Npcs[state.EntityId].RestoreWorldTransform(
                    state.Position.ToUnity(), state.Rotation.ToUnity());
            }
        }
        foreach (SaveGameInventoryState savedInventory in file.Inventories)
        {
            InventoryComponent inventory = world.Inventories[savedInventory.ContainerId];
            var items = new List<ItemData>(savedInventory.Slots.Count);
            var quantities = new List<int>(savedInventory.Slots.Count);
            foreach (SaveGameInventorySlotState slot in savedInventory.Slots)
            {
                items.Add(string.IsNullOrEmpty(slot.ItemId) ? null : world.Items[slot.ItemId]);
                quantities.Add(slot.Quantity);
            }
            if (!inventory.RestoreSlots(items, quantities, out string error))
                throw new InvalidDataException(error);
        }
    }

    private SaveGameFile Capture(string saveId, string displayName, string mode)
    {
        var file = new SaveGameFile
        {
            SaveId = saveId,
            DisplayName = displayName,
            SavedAt = DateTime.UtcNow,
            OperationId = Guid.NewGuid().ToString("D").ToLowerInvariant(),
            PendingConversationMode = mode,
            ConversationSynced = false,
            SceneName = SceneManager.GetActiveScene().name
        };
        file.Entities.Add(CaptureEntity(_player.WorldTargetId, _player.transform));
        var npcIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (NpcEntity npc in _player.NpcEntities)
        {
            if (npc == null || string.IsNullOrWhiteSpace(npc.npcId)) continue;
            string entityId = npc.npcId.Trim();
            if (!npcIds.Add(entityId)) throw new InvalidOperationException($"场景中存在重复 npcId: {entityId}");
            file.Entities.Add(CaptureEntity(entityId, npc.transform));
        }

        InventoryComponent[] inventories = UnityEngine.Object.FindObjectsByType<InventoryComponent>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        var containerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (InventoryComponent inventory in inventories.OrderBy(value => value.ContainerId, StringComparer.Ordinal))
        {
            string containerId = inventory.ContainerId;
            if (string.IsNullOrWhiteSpace(containerId)) throw new InvalidOperationException($"容器 '{inventory.name}' 缺少稳定 containerId。");
            if (!containerIds.Add(containerId)) throw new InvalidOperationException($"场景中存在重复 containerId: {containerId}");
            var state = new SaveGameInventoryState { ContainerId = containerId };
            foreach (InventorySlot slot in inventory.Slots)
            {
                state.Slots.Add(new SaveGameInventorySlotState
                {
                    ItemId = slot.IsEmpty ? null : slot.Item.ItemId,
                    Quantity = slot.IsEmpty ? 0 : slot.Quantity
                });
            }
            file.Inventories.Add(state);
        }
        return file;
    }

    private ValidatedWorld ValidateWorld(SaveGameFile file)
    {
        ValidateIdentity(file);
        if (!string.Equals(file.SceneName, SceneManager.GetActiveScene().name, StringComparison.Ordinal))
            throw new InvalidDataException($"存档场景 '{file.SceneName}' 与当前场景不一致。");
        var world = new ValidatedWorld();
        foreach (NpcEntity npc in _player.NpcEntities)
        {
            if (npc == null || string.IsNullOrWhiteSpace(npc.npcId)) continue;
            world.Npcs.Add(npc.npcId.Trim(), npc);
        }
        world.Items = BuildItemMap();
        foreach (InventoryComponent inventory in UnityEngine.Object.FindObjectsByType<InventoryComponent>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!string.IsNullOrWhiteSpace(inventory.ContainerId)) world.Inventories.Add(inventory.ContainerId, inventory);
        }
        var entityIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (SaveGameEntityState entity in file.Entities ?? new List<SaveGameEntityState>())
        {
            if (entity == null || string.IsNullOrWhiteSpace(entity.EntityId) || !entityIds.Add(entity.EntityId))
                throw new InvalidDataException("存档包含空或重复的 entityId。");
            if (entity.EntityId != _player.WorldTargetId && !world.Npcs.ContainsKey(entity.EntityId))
                throw new InvalidDataException($"当前世界缺少 NPC: {entity.EntityId}");
        }
        if (!entityIds.Contains(_player.WorldTargetId) || entityIds.Count != world.Npcs.Count + 1)
            throw new InvalidDataException("存档中的玩家/NPC 集合与当前世界不一致。");
        var containerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (SaveGameInventoryState inventory in file.Inventories ?? new List<SaveGameInventoryState>())
        {
            if (inventory == null || string.IsNullOrWhiteSpace(inventory.ContainerId) || !containerIds.Add(inventory.ContainerId))
                throw new InvalidDataException("存档包含空或重复的 containerId。");
            if (!world.Inventories.TryGetValue(inventory.ContainerId, out InventoryComponent current))
                throw new InvalidDataException($"当前世界缺少容器: {inventory.ContainerId}");
            if (inventory.Slots == null || inventory.Slots.Count != current.Slots.Count)
                throw new InvalidDataException($"容器 '{inventory.ContainerId}' 的格子数量不一致。");
            foreach (SaveGameInventorySlotState slot in inventory.Slots)
            {
                if (slot == null || (string.IsNullOrEmpty(slot.ItemId) && slot.Quantity != 0))
                    throw new InvalidDataException($"容器 '{inventory.ContainerId}' 包含无效空格子。");
                if (!string.IsNullOrEmpty(slot.ItemId))
                {
                    if (!world.Items.TryGetValue(slot.ItemId, out ItemData item))
                        throw new InvalidDataException($"物品目录缺少 itemId: {slot.ItemId}");
                    if (slot.Quantity <= 0 || slot.Quantity > item.MaxStackSize)
                        throw new InvalidDataException($"物品 '{slot.ItemId}' 的数量无效。");
                }
            }
        }
        if (containerIds.Count != world.Inventories.Count)
            throw new InvalidDataException("存档中的容器集合与当前世界不一致。");
        return world;
    }

    private void ValidateIdentity(SaveGameFile file)
    {
        if (file == null || file.Version != 1) throw new InvalidDataException("世界存档版本不受支持。");
        if (!IsCanonicalUuid(file.SaveId) || !IsCanonicalUuid(file.OperationId)) throw new InvalidDataException("世界存档标识无效。");
        if (string.IsNullOrWhiteSpace(file.DisplayName) || file.SavedAt == default || string.IsNullOrWhiteSpace(file.SceneName))
            throw new InvalidDataException("世界存档元数据不完整。");
        if (file.PendingConversationMode != "create" && file.PendingConversationMode != "overwrite")
            throw new InvalidDataException("世界存档对话同步模式无效。");
    }

    private Dictionary<string, ItemData> BuildItemMap()
    {
        var result = new Dictionary<string, ItemData>(StringComparer.Ordinal);
        if (_itemCatalog?.items == null) return result;
        foreach (ItemData item in _itemCatalog.items)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.ItemId)) continue;
            result.Add(item.ItemId, item);
        }
        return result;
    }

    private void WriteFile(SaveGameFile file, bool overwrite)
    {
        Directory.CreateDirectory(_directory);
        string target = GetPath(file.SaveId);
        if (!overwrite && File.Exists(target)) throw new IOException("同 saveId 的世界存档已存在。");
        string temp = Path.Combine(_directory, file.SaveId + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(file, Formatting.Indented));
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (overwrite)
                File.Replace(temp, target, null);
            else
                File.Move(temp, target);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private SaveGameFile ReadFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("世界存档不存在。", path);
        return JsonConvert.DeserializeObject<SaveGameFile>(File.ReadAllText(path));
    }

    private string GetPath(string saveId) => Path.Combine(_directory, saveId + ".json");
    private static bool IsCanonicalUuid(string value) => Guid.TryParseExact(value, "D", out Guid parsed) && value == parsed.ToString("D").ToLowerInvariant();
    private static string NormalizeDisplayName(string value)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : value.Trim();
        return normalized.Length <= 64 ? normalized : normalized.Substring(0, 64);
    }
    private static SaveGameEntityState CaptureEntity(string id, Transform transform) => new SaveGameEntityState { EntityId = id, Position = new SaveGameVector3(transform.position), Rotation = new SaveGameQuaternion(transform.rotation) };

    private sealed class ValidatedWorld
    {
        public Dictionary<string, NpcEntity> Npcs = new Dictionary<string, NpcEntity>(StringComparer.Ordinal);
        public Dictionary<string, InventoryComponent> Inventories = new Dictionary<string, InventoryComponent>(StringComparer.Ordinal);
        public Dictionary<string, ItemData> Items = new Dictionary<string, ItemData>(StringComparer.Ordinal);
    }
}