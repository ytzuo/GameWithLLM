using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;

public sealed class RemoteNpcRoundThreeTests
{
    [Test]
    public void VersionTwoSaveRoundTripsRemoteNpcContentIdentity()
    {
        Type fileType = RuntimeType("SaveGameFile");
        Type bindingType = RuntimeType("SaveGameNpcContentState");
        object file = Activator.CreateInstance(fileType);
        Set(file, "SaveId", Guid.NewGuid().ToString("D").ToLowerInvariant());
        Set(file, "DisplayName", "remote npc save");
        Set(file, "SavedAt", DateTime.UtcNow);
        Set(file, "OperationId", Guid.NewGuid().ToString("D").ToLowerInvariant());
        Set(file, "PendingConversationMode", "create");
        Set(file, "SceneName", "SampleScene");
        object binding = Activator.CreateInstance(bindingType);
        Set(binding, "EntityId", "merchant_001");
        Set(binding, "ContentVersion", "1");
        Set(binding, "ManifestSha256", new string('a', 64));
        ((IList)fileType.GetField("NpcContents").GetValue(file)).Add(binding);

        object restored = JsonConvert.DeserializeObject(JsonConvert.SerializeObject(file), fileType);
        IList contents = (IList)fileType.GetField("NpcContents").GetValue(restored);

        Assert.AreEqual(2, fileType.GetField("Version").GetValue(restored));
        Assert.AreEqual("merchant_001", bindingType.GetField("EntityId").GetValue(contents[0]));
        Assert.AreEqual(new string('a', 64), bindingType.GetField("ManifestSha256").GetValue(contents[0]));
    }

    [Test]
    public void RestoreRequirementsRejectDuplicateAndIncompleteBindings()
    {
        Type bindingType = RuntimeType("SaveGameNpcContentState");
        Type listType = typeof(System.Collections.Generic.List<>).MakeGenericType(bindingType);
        MethodInfo validate = RuntimeType("NpcLibraryServices").GetMethod(
            "ValidateRestoreRequirements", BindingFlags.Public | BindingFlags.Static);
        object valid = Activator.CreateInstance(bindingType);
        Set(valid, "EntityId", "merchant_001");
        Set(valid, "ContentVersion", "1");
        Set(valid, "ManifestSha256", new string('a', 64));
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
