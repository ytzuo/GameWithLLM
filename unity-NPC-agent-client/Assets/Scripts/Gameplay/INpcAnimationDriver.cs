using System;
using UnityEngine;

// Called exclusively on the Unity main thread. No entity, tool or network ownership.
[UnityEngine.Scripting.Preserve]
public interface INpcAnimationDriver : IDisposable
{
    void Bind(Animator animator);
    void Tick(float deltaTime, float movementSpeed);
}
