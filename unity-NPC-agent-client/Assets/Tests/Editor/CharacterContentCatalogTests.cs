using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;

public sealed class CharacterContentCatalogTests
{
    private const string ValidJson = @"{
      'schemaVersion': 1,
      'contentVersion': 'a5-v1',
      'appearances': [{
        'characterId': 'alice',
        'appearanceId': 'default',
        'prefabAddress': 'character/alice/visual/default',
        'avatarAddress': null,
        'animatorControllerAddress': 'character/alice/animator/default',
        'animationSetAddresses': ['character/alice/animation/idle'],
        'materialAddresses': ['character/alice/material/default-body'],
        'textureAddresses': ['character/alice/texture/default-body']
      }]
    }";

    [Test]
    public void Parse_AcceptsStableCharacterAppearanceMapping()
    {
        var parsed = CharacterContentCatalog.Parse(ValidJson);
        CharacterAppearanceDefinition appearance = parsed["alice/default"];
        Assert.That(appearance.PrefabAddress, Is.EqualTo("character/alice/visual/default"));
        Assert.That(appearance.AnimationSetAddresses.Count, Is.EqualTo(1));
    }

    [Test]
    public void Parse_RejectsCrossCharacterDependency()
    {
        string invalid = ValidJson.Replace(
            "character/alice/texture/default-body",
            "character/ryan/texture/default-body");
        Assert.Throws<InvalidOperationException>(() => CharacterContentCatalog.Parse(invalid));
    }

    [Test]
    public void ValidateVisualInstance_RejectsAuthoritativeComponent()
    {
        var visual = new GameObject("Visual");
        visual.AddComponent<NavMeshAgent>();
        Assert.Throws<InvalidOperationException>(() =>
            CharacterVisualController.ValidateVisualInstance(visual));
        UnityEngine.Object.DestroyImmediate(visual);
    }

    [Test]
    public void ValidateVisualInstance_AcceptsRendererAndAnimatorOnly()
    {
        GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
        UnityEngine.Object.DestroyImmediate(visual.GetComponent<Collider>());
        visual.AddComponent<Animator>();
        Assert.DoesNotThrow(() => CharacterVisualController.ValidateVisualInstance(visual));
        UnityEngine.Object.DestroyImmediate(visual);
    }
}
