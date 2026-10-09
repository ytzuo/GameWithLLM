using System;
using UnityEngine;

// Stable AOT driver used by ordinary schema-v2 NPC Prefabs. Conversation-state
// parameters are reserved here and become event-driven in iteration two.
[UnityEngine.Scripting.Preserve]
public sealed class StandardLocomotionAnimationDriver : INpcAnimationDriver
{
    public const string DriverId = "standard-locomotion";
    private static readonly int SpeedParameter = Animator.StringToHash("Speed");
    private static readonly int MovingParameter = Animator.StringToHash("Moving");
    private static readonly int ThinkingParameter = Animator.StringToHash("Thinking");
    private static readonly int SpeakingParameter = Animator.StringToHash("Speaking");

    private Animator _animator;

    public static INpcAnimationDriver Create(string driverId)
    {
        if (!string.Equals(driverId, DriverId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unknown builtin NPC animation driver '{driverId}'.");
        return new StandardLocomotionAnimationDriver();
    }

    public void Bind(Animator animator)
    {
        _animator = animator != null
            ? animator
            : throw new ArgumentNullException(nameof(animator));
        bool hasSpeed = false;
        bool hasMoving = false;
        bool hasThinking = false;
        bool hasSpeaking = false;
        foreach (AnimatorControllerParameter parameter in animator.parameters)
        {
            hasSpeed |= parameter.nameHash == SpeedParameter &&
                        parameter.type == AnimatorControllerParameterType.Float;
            hasMoving |= parameter.nameHash == MovingParameter &&
                         parameter.type == AnimatorControllerParameterType.Bool;
            hasThinking |= parameter.nameHash == ThinkingParameter &&
                           parameter.type == AnimatorControllerParameterType.Bool;
            hasSpeaking |= parameter.nameHash == SpeakingParameter &&
                           parameter.type == AnimatorControllerParameterType.Bool;
        }
        if (!hasSpeed || !hasMoving || !hasThinking || !hasSpeaking)
        {
            _animator = null;
            throw new InvalidOperationException(
                "standard-locomotion requires Speed(float), Moving(bool), Thinking(bool), and Speaking(bool) Animator parameters.");
        }
    }

    public void Tick(float deltaTime, float movementSpeed)
    {
        if (_animator != null)
            _animator.SetFloat(SpeedParameter, Mathf.Max(0f, movementSpeed));
    }

    public void Dispose() => _animator = null;
}
