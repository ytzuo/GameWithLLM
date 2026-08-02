namespace GameWithLLM.AgentRuntime
{
    public abstract class AgentResponseEvent { }
    public sealed class ResponseStarted : AgentResponseEvent
    {
        public string TaskId { get; }
        public string ContextId { get; }
        public ResponseStarted(string taskId, string contextId) { TaskId = taskId; ContextId = contextId; }
    }
    public sealed class TextDelta : AgentResponseEvent
    {
        public string Text { get; }
        public bool Reset { get; }
        public TextDelta(string text, bool reset = false) { Text = text; Reset = reset; }
    }
    public sealed class StatusChanged : AgentResponseEvent
    {
        public string Status { get; }
        public StatusChanged(string status) { Status = status; }
    }
    public sealed class ResponseCompleted : AgentResponseEvent
    {
        public string FinalText { get; }
        public string ContextId { get; }
        public ResponseCompleted(string finalText, string contextId) { FinalText = finalText; ContextId = contextId; }
    }
    public sealed class ResponseFailed : AgentResponseEvent
    {
        public string Code { get; }
        public string Message { get; }
        public ResponseFailed(string code, string message) { Code = code; Message = message; }
    }
}
