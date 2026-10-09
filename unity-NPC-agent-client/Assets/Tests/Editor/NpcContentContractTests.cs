using System;
using System.Collections.Generic;
using System.Reflection;
using GameWithLLM.AgentRuntime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

public sealed class NpcContentContractTests
{
    [Test]
    public void RuntimeWireManifestPreservesContentBindingAndLegacyFields()
    {
        var binding = new NpcContentBinding("merchant_001", "1", new string('a', 64));
        var manifest = new RuntimeManifest("game", new[] { "merchant_001" },
            Array.Empty<AgentToolDescriptor>(), 4, new[] { binding });
        MethodInfo serialize = typeof(RuntimeGatewayClient).GetMethod("ToManifest", BindingFlags.NonPublic | BindingFlags.Static);
        var wire = JObject.Parse(JsonConvert.SerializeObject(serialize.Invoke(null, new object[] { manifest })));
        Assert.AreEqual("merchant_001", (string)wire["entities"][0]);
        Assert.AreEqual("1", (string)wire["npcContents"][0]["contentVersion"]);
        Assert.AreEqual(new string('a', 64), (string)wire["npcContents"][0]["manifestSha256"]);
        Assert.IsNull(wire["profile"]);
        Assert.IsNull(wire["systemPrompt"]);
        using (var transport = new RuntimeGatewayClient("ws://localhost/runtime/ws", "test"))
        {
            typeof(RuntimeGatewayClient).GetMethod("SetManifest", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(transport, new object[] { manifest });
            var copy = (RuntimeManifest)typeof(RuntimeGatewayClient).GetMethod("GetManifest", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(transport, null);
            Assert.AreSame(binding, copy.NpcContents[0]);
        }
    }

    [Test]
    public void BindingRejectsPathsDuplicatesAndWrongEntities()
    {
        Assert.Throws<ArgumentException>(() => new NpcContentBinding("../npc", "1", new string('a', 64)));
        var binding = new NpcContentBinding("merchant_001", "1", new string('a', 64));
        Assert.Throws<ArgumentException>(() => new RuntimeManifest("game", new[] { "other" },
            Array.Empty<AgentToolDescriptor>(), 1, new[] { binding }));
        Assert.Throws<ArgumentException>(() => new RuntimeManifest("game", new[] { "merchant_001" },
            Array.Empty<AgentToolDescriptor>(), 1, new[] { binding, binding }));
        Assert.IsEmpty(new RuntimeManifest("game", new[] { "legacy" }, Array.Empty<AgentToolDescriptor>(), 1).NpcContents);
    }
}
