using System;
using UnityEngine;

public enum NpcAnimationDriverKind
{
    Builtin,
    HotUpdate
}

[Serializable]
public sealed class NpcProfileAuthoring
{
    public string displayName;
    public string[] personality = Array.Empty<string>();
    [TextArea] public string speakingStyle;
    [TextArea] public string identity;
    public string[] responsibilities = Array.Empty<string>();
    public string[] worldKnowledge = Array.Empty<string>();
    public string[] forbiddenTopics = Array.Empty<string>();
}

[Serializable]
public sealed class NpcHotUpdateDriverAuthoring
{
    public TextAsset assemblyBytes;
    public string assemblyName;
    public string entryType;
}

[CreateAssetMenu(
    fileName = "NpcContentDefinition",
    menuName = "GameWithLLM/NPC Content Definition")]
public sealed class NpcContentDefinition : ScriptableObject
{
    public string npcId;
    public string contentVersion;
    public string displayName;
    [TextArea] public string description;
    public string minPlayerVersion;
    public string maxPlayerVersion;
    public GameObject visualPrefab;
    public Texture2D avatar;
    public NpcAnimationDriverKind animationDriverKind = NpcAnimationDriverKind.Builtin;
    public string builtinDriverId = StandardLocomotionAnimationDriver.DriverId;
    public NpcHotUpdateDriverAuthoring hotUpdateDriver = new NpcHotUpdateDriverAuthoring();
    public NpcProfileAuthoring profile = new NpcProfileAuthoring();
    [TextArea(8, 40)] public string systemPromptTemplate;
}
