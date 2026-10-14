using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

// Stable event-driven AOT driver for current NPC Prefabs.
[UnityEngine.Scripting.Preserve]
public sealed class StandardLocomotionAnimationDriver : INpcAnimationDriver, INpcEventDrivenAnimationDriver
{
    public const string DriverId = "standard-locomotion";
    private static readonly int SpeedParameter = Animator.StringToHash("Speed");
    private static readonly int MovingParameter = Animator.StringToHash("Moving");
    private static readonly int ThinkingParameter = Animator.StringToHash("Thinking");
    private static readonly int SpeakingParameter = Animator.StringToHash("Speaking");

    private Animator _animator;
    private INpcAnimationEventSource _eventSource;

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
        // Dormant staging instances have no initialized Animator in IL2CPP.
        // Inspect the actual controller through a short-lived graph without
        // activating the entity or invoking its behaviours.
        if (animator.runtimeAnimatorController == null)
            throw new InvalidOperationException("NPC animation controller is missing.");
        var graph = PlayableGraph.Create("NPC controller validation");
        try
        {
            var controller = AnimatorControllerPlayable.Create(graph, animator.runtimeAnimatorController);
            for (int index = 0; index < controller.GetParameterCount(); index++)
            {
                AnimatorControllerParameter parameter = controller.GetParameter(index);
                hasSpeed |= parameter.nameHash == SpeedParameter &&
                            parameter.type == AnimatorControllerParameterType.Float;
                hasMoving |= parameter.nameHash == MovingParameter &&
                             parameter.type == AnimatorControllerParameterType.Bool;
                hasThinking |= parameter.nameHash == ThinkingParameter &&
                               parameter.type == AnimatorControllerParameterType.Bool;
                hasSpeaking |= parameter.nameHash == SpeakingParameter &&
                               parameter.type == AnimatorControllerParameterType.Bool;
            }
        }
        finally { graph.Destroy(); }
        if (!hasSpeed || !hasMoving || !hasThinking || !hasSpeaking)
        {
            _animator = null;
            throw new InvalidOperationException(
                "standard-locomotion requires Speed(float), Moving(bool), Thinking(bool), and Speaking(bool) Animator parameters.");
        }
    }

    public void Tick(float deltaTime, float movementSpeed)
    {
        if (_animator != null && _animator.isInitialized)
        {
            _animator.SetFloat(SpeedParameter, Mathf.Max(0f, movementSpeed));
            if (_eventSource != null) ApplySnapshot(_eventSource.AnimationSnapshot);
        }
    }

    public void BindEventSource(INpcAnimationEventSource eventSource)
    {
        if (eventSource == null) throw new ArgumentNullException(nameof(eventSource));
        if (_eventSource != null) _eventSource.AnimationEvent -= OnAnimationEvent;
        _eventSource = eventSource;
        _eventSource.AnimationEvent += OnAnimationEvent;
        ApplySnapshot(_eventSource.AnimationSnapshot);
    }

    private void OnAnimationEvent(NpcAnimationEvent animationEvent)
    {
        if (_eventSource != null) ApplySnapshot(_eventSource.AnimationSnapshot);
    }

    private void ApplySnapshot(NpcAnimationSnapshot snapshot)
    {
        if (_animator == null || !_animator.isInitialized) return;
        _animator.SetBool(MovingParameter, snapshot.IsMoving);
        _animator.SetBool(ThinkingParameter, snapshot.IsThinking);
        _animator.SetBool(SpeakingParameter, snapshot.IsSpeaking);
    }

    public void Dispose()
    {
        if (_eventSource != null) _eventSource.AnimationEvent -= OnAnimationEvent;
        _eventSource = null;
        _animator = null;
    }
}
