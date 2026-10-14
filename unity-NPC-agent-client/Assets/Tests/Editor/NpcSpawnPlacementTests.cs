using System;
using System.Reflection;
using NUnit.Framework;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.UIElements;

public sealed class NpcSpawnPlacementTests
{
    private GameObject _root;
    private GameObject _ground;
    private Camera _camera;
    private NavMeshSurface _surface;
    private readonly Vector3 _point = new Vector3(100, 0, 100);

    [SetUp]
    public void SetUp()
    {
        _root = new GameObject("spawn placement fixture");
        _ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        _ground.transform.SetParent(_root.transform);
        _ground.transform.position = _point;
        _surface = _root.AddComponent<NavMeshSurface>();
        _surface.collectObjects = CollectObjects.Children;
        _surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
        _surface.BuildNavMesh();
        _camera = new GameObject("spawn camera").AddComponent<Camera>();
        _camera.transform.SetParent(_root.transform);
        _camera.transform.position = _point + Vector3.up * 5f;
        _camera.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
        Physics.SyncTransforms();
    }

    [TearDown]
    public void TearDown()
    {
        var data = _surface.navMeshData;
        UnityEngine.Object.DestroyImmediate(_root);
        if (_ground != null) UnityEngine.Object.DestroyImmediate(_ground);
        if (data != null) UnityEngine.Object.DestroyImmediate(data);
    }

    [Test]
    public void CameraCenterHitSelectsNonOriginNavMeshPoint()
    {
        Assert.That(TryCamera(_camera, out Vector3 point), Is.True);
        Assert.That(Vector3.Distance(point, _point), Is.LessThan(0.15f));
        Assert.That(point.x, Is.GreaterThan(90f));
    }

    [Test]
    public void CameraWithoutHitIsRejected()
    {
        _camera.transform.rotation = Quaternion.LookRotation(Vector3.up);
        Assert.That(TryCamera(_camera, out _), Is.False);
    }

    [Test]
    public void MissingCameraIsRejected() => Assert.That(TryCamera(null, out _), Is.False);

    [Test]
    public void MissingColliderIsRejected()
    {
        UnityEngine.Object.DestroyImmediate(_ground.GetComponent<Collider>());
        Physics.SyncTransforms();
        Assert.That(TryCamera(_camera, out _), Is.False);
    }

    [Test]
    public void DisabledSurfaceIsRejected()
    {
        _surface.enabled = false;
        Assert.That(TryCamera(_camera, out _), Is.False);
    }

    [Test]
    public void HitOutsideSurfaceHierarchyIsRejectedEvenWithNearbyNavMesh()
    {
        _ground.transform.SetParent(null);
        Assert.That(TryCamera(_camera, out _), Is.False);
    }

    [Test]
    public void ForegroundColliderAwayFromNavMeshIsRejected()
    {
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.transform.SetParent(_root.transform);
        wall.transform.position = _point + Vector3.up * 2f;
        Physics.SyncTransforms();
        Assert.That(TryCamera(_camera, out _), Is.False);
    }

    [Test]
    public void NonFiniteRestorePositionIsRejected()
    {
        var args = new object[] { new Vector3(float.NaN, 0, 0), default(Vector3) };
        Assert.That(Placement.GetMethod("TrySamplePosition").Invoke(null, args), Is.False);
    }

    [Test]
    public void InvalidPositionPopupShowsRequestedMessage()
    {
        Type type = Type.GetType("NpcLibraryWindow, Assembly-CSharp", true);
        object window = Activator.CreateInstance(type);
        Type baseType = type.BaseType;
        var root = new VisualElement();
        baseType.GetProperty("RootElement").GetSetMethod(true).Invoke(window, new object[] { root });
        baseType.GetProperty("IsOpen").GetSetMethod(true).Invoke(window, new object[] { true });
        type.GetMethod("ShowInvalidPositionPopup", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, null);
        var popup = root.Q("npc-library-invalid-position-popup");
        Assert.That(popup, Is.Not.Null);
        Assert.That(popup.Q<Label>().text, Is.EqualTo("当前位置不是有效位置"));
        Assert.That(popup.Q<Button>().text, Is.EqualTo("确定"));
    }

    private static Type Placement => Type.GetType("NpcSpawnPlacement, Assembly-CSharp", true);

    private static bool TryCamera(Camera camera, out Vector3 point)
    {
        var args = new object[] { camera, default(Vector3) };
        bool valid = (bool)Placement.GetMethod("TryFromCamera").Invoke(null, args);
        point = (Vector3)args[1];
        return valid;
    }
}
