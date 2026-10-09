using System;
using UnityEngine;

// Called exclusively on the Unity main thread. No entity, tool or network ownership.
[UnityEngine.Scripting.Preserve]
public interface INpcAnimationDriver : IDisposable
{
    void Bind(Animator animator);
    void Tick(float deltaTime, float movementSpeed);
}

public enum NpcAnimationEventType
{
    MovementStarted,
    MovementEnded,
    ThinkingStarted,
    ThinkingEnded,
    SpeakingStarted,
    SpeakingEnded
}

public enum NpcAnimationEndReason
{
    None,
    TextReceived,
    Completed,
    Failed,
    Cancelled,
    WorldRestored,
    SceneChanged,
    Superseded,
    Destroyed
}

public sealed class NpcAnimationEvent
{
    public NpcAnimationEventType Type { get; }
    public string OperationId { get; }
    public NpcAnimationEndReason EndReason { get; }

    public NpcAnimationEvent(
        NpcAnimationEventType type,
        string operationId,
        NpcAnimationEndReason endReason = NpcAnimationEndReason.None)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("An animation operation ID is required.", nameof(operationId));
        bool isEnd = type == NpcAnimationEventType.MovementEnded ||
                     type == NpcAnimationEventType.ThinkingEnded ||
                     type == NpcAnimationEventType.SpeakingEnded;
        if (isEnd == (endReason == NpcAnimationEndReason.None))
            throw new ArgumentException("Only animation end events require an end reason.", nameof(endReason));
        Type = type;
        OperationId = operationId;
        EndReason = endReason;
    }
}

public readonly struct NpcAnimationSnapshot
{
    public bool IsMoving { get; }
    public bool IsThinking { get; }
    public bool IsSpeaking { get; }

    public NpcAnimationSnapshot(bool isMoving, bool isThinking, bool isSpeaking)
    {
        IsMoving = isMoving;
        IsThinking = isThinking;
        IsSpeaking = isSpeaking;
    }
}

public interface INpcAnimationEventSource
{
    event Action<NpcAnimationEvent> AnimationEvent;
    NpcAnimationSnapshot AnimationSnapshot { get; }
}

// Optional side contract. Existing v1 drivers only implement INpcAnimationDriver
// and remain compatible; event-aware drivers bind after their normal Bind call.
public interface INpcEventDrivenAnimationDriver
{
    void BindEventSource(INpcAnimationEventSource eventSource);
}

// Main-thread presentation state machine shared by movement and conversation.
// The two dimensions are deliberately independent so their Animator flags can overlap.
public sealed class NpcAnimationEventSource : INpcAnimationEventSource
{
    public event Action<NpcAnimationEvent> AnimationEvent;

    public NpcAnimationSnapshot AnimationSnapshot => new NpcAnimationSnapshot(
        _movementOperationId != null,
        _thinkingOperationId != null,
        _speakingOperationId != null);

    private string _movementOperationId;
    private string _thinkingOperationId;
    private string _speakingOperationId;
    private float _speakingStartedAt;
    private float? _speakingEndsAt;

    public bool StartMovement(string operationId)
    {
        if (_movementOperationId != null) return false;
        _movementOperationId = RequireOperationId(operationId);
        Publish(NpcAnimationEventType.MovementStarted, operationId);
        return true;
    }

    public bool EndMovement(string operationId, NpcAnimationEndReason reason)
    {
        if (!Matches(_movementOperationId, operationId)) return false;
        _movementOperationId = null;
        Publish(NpcAnimationEventType.MovementEnded, operationId, RequireEndReason(reason));
        return true;
    }

    public bool StartResponse(string operationId)
    {
        operationId = RequireOperationId(operationId);
        string previous = _thinkingOperationId ?? _speakingOperationId;
        if (previous != null) EndResponse(previous, NpcAnimationEndReason.Superseded);
        _thinkingOperationId = operationId;
        Publish(NpcAnimationEventType.ThinkingStarted, operationId);
        return true;
    }

    public bool ReceiveText(string operationId, string text, float now)
    {
        if (string.IsNullOrEmpty(text) || !Matches(_thinkingOperationId, operationId)) return false;
        EndThinking(operationId, NpcAnimationEndReason.TextReceived);
        StartSpeaking(operationId, now);
        return true;
    }

    public bool CompleteResponse(
        string operationId,
        string finalText,
        float now,
        float charactersPerSecond,
        float minimumSeconds,
        float maximumSeconds)
    {
        bool isThinking = Matches(_thinkingOperationId, operationId);
        bool isSpeaking = Matches(_speakingOperationId, operationId);
        if (!isThinking && !isSpeaking) return false;

        if (isThinking)
        {
            EndThinking(operationId, NpcAnimationEndReason.Completed);
            if (CountVisibleCharacters(finalText) > 0)
                StartSpeaking(operationId, now);
        }

        if (!Matches(_speakingOperationId, operationId)) return true;
        float safeRate = Math.Max(0.01f, charactersPerSecond);
        float expected = Math.Min(
            Math.Max(CountVisibleCharacters(finalText) / safeRate, Math.Max(0f, minimumSeconds)),
            Math.Max(minimumSeconds, maximumSeconds));
        float remaining = Math.Max(0f, expected - Math.Max(0f, now - _speakingStartedAt));
        if (remaining <= 0f)
            EndSpeaking(operationId, NpcAnimationEndReason.Completed);
        else
            _speakingEndsAt = now + remaining;
        return true;
    }

    public bool EndResponse(string operationId, NpcAnimationEndReason reason)
    {
        bool changed = false;
        if (Matches(_thinkingOperationId, operationId))
        {
            EndThinking(operationId, reason);
            changed = true;
        }
        if (Matches(_speakingOperationId, operationId))
        {
            EndSpeaking(operationId, reason);
            changed = true;
        }
        return changed;
    }

    public void Tick(float now)
    {
        if (_speakingEndsAt.HasValue && now >= _speakingEndsAt.Value)
            EndSpeaking(_speakingOperationId, NpcAnimationEndReason.Completed);
    }

    public void EndAll(NpcAnimationEndReason reason)
    {
        string movement = _movementOperationId;
        string response = _thinkingOperationId ?? _speakingOperationId;
        if (movement != null) EndMovement(movement, reason);
        if (response != null) EndResponse(response, reason);
    }

    private void StartSpeaking(string operationId, float now)
    {
        _speakingOperationId = operationId;
        _speakingStartedAt = now;
        _speakingEndsAt = null;
        Publish(NpcAnimationEventType.SpeakingStarted, operationId);
    }

    private void EndThinking(string operationId, NpcAnimationEndReason reason)
    {
        _thinkingOperationId = null;
        Publish(NpcAnimationEventType.ThinkingEnded, operationId, RequireEndReason(reason));
    }

    private void EndSpeaking(string operationId, NpcAnimationEndReason reason)
    {
        _speakingOperationId = null;
        _speakingStartedAt = 0f;
        _speakingEndsAt = null;
        Publish(NpcAnimationEventType.SpeakingEnded, operationId, RequireEndReason(reason));
    }

    private void Publish(
        NpcAnimationEventType type,
        string operationId,
        NpcAnimationEndReason reason = NpcAnimationEndReason.None) =>
        AnimationEvent?.Invoke(new NpcAnimationEvent(type, operationId, reason));

    private static bool Matches(string activeOperationId, string operationId) =>
        activeOperationId != null && string.Equals(activeOperationId, operationId, StringComparison.Ordinal);

    private static string RequireOperationId(string operationId) =>
        !string.IsNullOrWhiteSpace(operationId)
            ? operationId
            : throw new ArgumentException("An animation operation ID is required.", nameof(operationId));

    private static NpcAnimationEndReason RequireEndReason(NpcAnimationEndReason reason) =>
        reason != NpcAnimationEndReason.None
            ? reason
            : throw new ArgumentException("An animation end reason is required.", nameof(reason));

    private static int CountVisibleCharacters(string text)
    {
        int count = 0;
        foreach (char character in text ?? string.Empty)
            if (!char.IsWhiteSpace(character) && !char.IsControl(character)) count++;
        return count;
    }
}
