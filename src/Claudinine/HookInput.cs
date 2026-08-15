using System.Text.Json.Serialization;

namespace Claudinine;

/// <summary>
/// Common envelope Claude Code sends on stdin to every hook. Event-specific
/// fields are optional; unknown fields are ignored by design (format tolerance).
/// </summary>
internal sealed class HookInput
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("transcript_path")]
    public string? TranscriptPath { get; set; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; set; }

    [JsonPropertyName("hook_event_name")]
    public string? HookEventName { get; set; }

    /// <summary>UserPromptSubmit: the prompt text.</summary>
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    /// <summary>SessionStart: startup | resume | clear | compact.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>SessionEnd: why the session ended.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>PreCompact: manual | auto.</summary>
    [JsonPropertyName("trigger")]
    public string? Trigger { get; set; }

    /// <summary>SubagentStop: the finished agent's own transcript file.</summary>
    [JsonPropertyName("agent_transcript_path")]
    public string? AgentTranscriptPath { get; set; }
}

/// <summary>
/// What Claude Code sends on stdin to a `statusLine` command. A different
/// envelope from <see cref="HookInput"/>, and the only one carrying live context
/// usage — hooks never see token counts. Fields we do not use are omitted;
/// unknown ones are ignored by design, as with every input we parse.
/// </summary>
internal sealed class StatuslineInput
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("transcript_path")]
    public string? TranscriptPath { get; set; }

    [JsonPropertyName("context_window")]
    public ContextWindow? ContextWindow { get; set; }
}

/// <summary>
/// Live context usage, from the most recent API response — the number no hook
/// can observe. Absent before the first response of a session.
/// </summary>
internal sealed class ContextWindow
{
    /// <summary>Tokens currently in the window; includes cache reads and writes.</summary>
    [JsonPropertyName("total_input_tokens")]
    public int? TotalInputTokens { get; set; }

    [JsonPropertyName("used_percentage")]
    public double? UsedPercentage { get; set; }
}

[JsonSourceGenerationOptions(ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
[JsonSerializable(typeof(HookInput))]
[JsonSerializable(typeof(StatuslineInput))]
internal sealed partial class ClaudinineJsonContext : JsonSerializerContext;
