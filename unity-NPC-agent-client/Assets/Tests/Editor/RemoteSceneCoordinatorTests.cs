using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class RemoteSceneCoordinatorTests
{
    [Test]
    public void ValidateRemoteSceneBoundary_AcceptsWorldOnlyScene()
    {
        Scene scene = SceneManager.GetActiveScene();
        var root = new GameObject("World");
        try
        {
            Assert.DoesNotThrow(() => RemoteSceneCoordinator.ValidateRemoteSceneBoundary(scene));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void ValidateRemoteSceneBoundary_RejectsPersistentRuntimeComponent()
    {
        Scene scene = SceneManager.GetActiveScene();
        var root = new GameObject("DuplicateRuntime");
        root.AddComponent<RemoteSceneCoordinator>();
        try
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => RemoteSceneCoordinator.ValidateRemoteSceneBoundary(scene));
            StringAssert.Contains(nameof(RemoteSceneCoordinator), error.Message);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }
}
