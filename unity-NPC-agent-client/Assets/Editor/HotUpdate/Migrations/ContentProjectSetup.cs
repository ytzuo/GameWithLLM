using UnityEditor;
using UnityEngine;

public static class ContentProjectSetup
{
    [MenuItem("GameWithLLM/Content/Project Setup/Repair Configuration")]
    public static void RepairConfiguration()
    {
        HybridClrProjectSetup.Configure();
        ContentDeliveryProjectSetup.Configure();
        UiContentSetup.Configure();
        ItemContentSetup.Configure();
        CharacterContentSetup.Configure();
        SceneContentSetup.Configure();
        AssetDatabase.SaveAssets();
        Debug.Log("[Content] Project configuration and content ownership repaired.");
    }
}
