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
}

[JsonSourceGenerationOptions(ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
[JsonSerializable(typeof(HookInput))]
internal sealed partial class ClaudinineJsonContext : JsonSerializerContext;
