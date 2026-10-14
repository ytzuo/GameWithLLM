using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;

public sealed class RemoteNpcRoundThreeTests
{
    [Test]
    public void CurrentSaveRoundTripsRemoteNpcContentIdentity()
    {
        Type fileType = RuntimeType("SaveGameFile");
        Type bindingType = RuntimeType("SaveGameNpcContentState");
        object file = Activator.CreateInstance(fileType);
        Set(file, "Version", 3);
        Set(file, "SaveId", Guid.NewGuid().ToString("D").ToLowerInvariant());
        Set(file, "DisplayName", "remote npc save");
        Set(file, "SavedAt", DateTime.UtcNow);
        Set(file, "OperationId", Guid.NewGuid().ToString("D").ToLowerInvariant());
        Set(file, "PendingConversationMode", "create");
        Set(file, "SceneName", "SampleScene");
        object binding = Activator.CreateInstance(bindingType);
        Set(binding, "EntityId", "Merchant");
        ((IList)fileType.GetField("NpcContents").GetValue(file)).Add(binding);

        object restored = JsonConvert.DeserializeObject(JsonConvert.SerializeObject(file), fileType);
        IList contents = (IList)fileType.GetField("NpcContents").GetValue(restored);

        Assert.AreEqual(3, fileType.GetField("Version").GetValue(restored));
        Assert.AreEqual("Merchant", bindingType.GetField("EntityId").GetValue(contents[0]));
    }

    [Test]
    public void RestoreRequirementsRejectDuplicateAndIncompleteBindings()
    {
        Type bindingType = RuntimeType("SaveGameNpcContentState");
        Type listType = typeof(System.Collections.Generic.List<>).MakeGenericType(bindingType);
        MethodInfo validate = RuntimeType("NpcLibraryServices").GetMethod(
            "ValidateRestoreRequirements", BindingFlags.Public | BindingFlags.Static);
        object valid = Activator.CreateInstance(bindingType);
        Set(valid, "EntityId", "Merchant");
        IList one = (IList)Activator.CreateInstance(listType);
        one.Add(valid);
        Assert.DoesNotThrow(() => validate.Invoke(null, new[] { one }));

        IList duplicate = (IList)Activator.CreateInstance(listType);
        duplicate.Add(valid); duplicate.Add(valid);
        AssertInvalidData(validate, duplicate);
        IList incomplete = (IList)Activator.CreateInstance(listType);
        incomplete.Add(Activator.CreateInstance(bindingType));
        AssertInvalidData(validate, incomplete);
    }

    [Test]
    public void OldAndMissingWorldSaveFormatsAreExplicitlyRejected()
    {
        Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject("{\"version\":3}", RuntimeType("SaveGameFile")));
        Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject("{\"version\":3,\"npcContents\":null}", RuntimeType("SaveGameFile")));
        Type serviceType = RuntimeType("SaveGameService");
        object service = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(serviceType);
        MethodInfo validate = serviceType.GetMethod("ValidateIdentity", BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (int version in new[] { 0, 1, 2, 4 })
        {
            object file = Activator.CreateInstance(RuntimeType("SaveGameFile"));
            Set(file, "Version", version);
            var error = Assert.Throws<TargetInvocationException>(() => validate.Invoke(service, new[] { file }));
            Assert.IsInstanceOf<InvalidDataException>(error.InnerException);
            StringAssert.Contains("NPC_SAVE_FORMAT_UNSUPPORTED", error.InnerException.Message);
        }
    }

    private static Type RuntimeType(string name) =>
        Type.GetType(name + ", Assembly-CSharp", true);

    private static void Set(object target, string field, object value) =>
        target.GetType().GetField(field).SetValue(target, value);

    private static void AssertInvalidData(MethodInfo method, object argument)
    {
        TargetInvocationException error = Assert.Throws<TargetInvocationException>(
            () => method.Invoke(null, new[] { argument }));
        Assert.IsInstanceOf<InvalidDataException>(error.InnerException);
    }
}
