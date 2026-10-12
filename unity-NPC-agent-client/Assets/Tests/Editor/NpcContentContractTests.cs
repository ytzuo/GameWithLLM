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
    public void AnimationEventsKeepMovementAndConversationIndependent()
    {
        var source = new NpcAnimationEventSource();
        var events = new List<NpcAnimationEvent>();
        source.AnimationEvent += events.Add;

        Assert.IsTrue(source.StartMovement("move-1"));
        Assert.IsTrue(source.StartResponse("response-1"));
        Assert.That(source.AnimationSnapshot.IsMoving, Is.True);
        Assert.That(source.AnimationSnapshot.IsThinking, Is.True);
        Assert.That(source.AnimationSnapshot.IsSpeaking, Is.False);

        Assert.IsTrue(source.ReceiveText("response-1", "你好", 2f));
        Assert.That(source.AnimationSnapshot.IsMoving, Is.True);
        Assert.That(source.AnimationSnapshot.IsThinking, Is.False);
        Assert.That(source.AnimationSnapshot.IsSpeaking, Is.True);
        Assert.IsTrue(source.EndMovement("move-1", NpcAnimationEndReason.Completed));
        Assert.IsFalse(source.EndMovement("move-1", NpcAnimationEndReason.Failed));
        Assert.That(source.AnimationSnapshot.IsSpeaking, Is.True);

        CollectionAssert.AreEqual(
            new[]
            {
                NpcAnimationEventType.MovementStarted,
                NpcAnimationEventType.ThinkingStarted,
                NpcAnimationEventType.ThinkingEnded,
                NpcAnimationEventType.SpeakingStarted,
                NpcAnimationEventType.MovementEnded
            },
            events.ConvertAll(value => value.Type));
    }

    [Test]
    public void StreamingSpeakingTimerCountsElapsedTimeAndIgnoresLateEvents()
    {
        var source = new NpcAnimationEventSource();
        var events = new List<NpcAnimationEvent>();
        source.AnimationEvent += events.Add;

        source.StartResponse("response-1");
        source.ReceiveText("response-1", "第一段", 10f);
        source.CompleteResponse("response-1", "一二三四五六", 12f, 2f, 0.5f, 10f);
        source.Tick(12.9f);
        Assert.That(source.AnimationSnapshot.IsSpeaking, Is.True);
        source.Tick(13f);
        Assert.That(source.AnimationSnapshot.IsSpeaking, Is.False);

        Assert.IsFalse(source.ReceiveText("response-1", "迟到文本", 14f));
        Assert.IsFalse(source.CompleteResponse("response-1", "迟到完成", 14f, 2f, 0.5f, 10f));
        Assert.That(events.FindAll(value => value.Type == NpcAnimationEventType.SpeakingEnded).Count, Is.EqualTo(1));
        Assert.That(events[events.Count - 1].EndReason, Is.EqualTo(NpcAnimationEndReason.Completed));
    }

    [Test]
    public void FinalOnlyResponseStartsSpeakingAndCancellationClosesStateOnce()
    {
        var source = new NpcAnimationEventSource();
        var events = new List<NpcAnimationEvent>();
        source.AnimationEvent += events.Add;

        source.StartResponse("response-1");
        Assert.IsTrue(source.CompleteResponse("response-1", "最终正文", 5f, 10f, 1f, 8f));
        Assert.That(source.AnimationSnapshot.IsThinking, Is.False);
        Assert.That(source.AnimationSnapshot.IsSpeaking, Is.True);
        Assert.IsTrue(source.EndResponse("response-1", NpcAnimationEndReason.Cancelled));
        Assert.IsFalse(source.EndResponse("response-1", NpcAnimationEndReason.Failed));
        Assert.That(source.AnimationSnapshot.IsSpeaking, Is.False);
        Assert.That(events.FindAll(value => value.Type == NpcAnimationEventType.SpeakingEnded).Count, Is.EqualTo(1));
    }

    [Test]
    public void NewResponseSupersedesEstimatedSpeakingWithoutAcceptingLateCompletion()
    {
        var source = new NpcAnimationEventSource();
        var events = new List<NpcAnimationEvent>();
        source.AnimationEvent += events.Add;

        source.StartResponse("response-1");
        source.CompleteResponse("response-1", "一段仍在播放的较长正文", 1f, 2f, 0.5f, 10f);
        Assert.That(source.AnimationSnapshot.IsSpeaking, Is.True);

        Assert.IsTrue(source.StartResponse("response-2"));
        Assert.That(source.AnimationSnapshot.IsSpeaking, Is.False);
        Assert.That(source.AnimationSnapshot.IsThinking, Is.True);
        Assert.IsFalse(source.CompleteResponse("response-1", "迟到完成", 2f, 2f, 0.5f, 10f));
        Assert.That(events.Find(value =>
            value.Type == NpcAnimationEventType.SpeakingEnded &&
            value.OperationId == "response-1").EndReason, Is.EqualTo(NpcAnimationEndReason.Superseded));
    }

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
