using System.Text;
using System.Text.Json.Nodes;

namespace Claudinine.Tests;

/// <summary>Builds synthetic transcript JSONL shaped like the app's records.</summary>
internal sealed class TranscriptBuilder
{
    private readonly List<string> _lines = [];
    private string? _lastUuid;
    private int _seq;

    public string NextUuid() => $"00000000-0000-0000-0000-{++_seq:D12}";

    public TranscriptBuilder UserPrompt(string text)
    {
        Add("user", new JsonObject { ["role"] = "user", ["content"] = text });
        return this;
    }

    public TranscriptBuilder BashRead(string command, out string toolUseId, string longOutput)
    {
        toolUseId = $"toolu_{_seq + 1:D4}";
        Add("assistant", new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = toolUseId,
                ["name"] = "Bash",
                ["input"] = new JsonObject { ["command"] = command },
            }),
        });
        Add("user", new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = toolUseId,
                ["content"] = longOutput,
            }),
        });
        return this;
    }

    public TranscriptBuilder ToolRead(string filePath, out string toolUseId, string longOutput,
        int? offset = null, int? limit = null)
    {
        toolUseId = $"toolu_{_seq + 1:D4}";
        var input = new JsonObject { ["file_path"] = filePath };
        if (offset is not null) input["offset"] = offset;
        if (limit is not null) input["limit"] = limit;
        Add("assistant", new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = toolUseId,
                ["name"] = "Read",
                ["input"] = input,
            }),
        });
        Add("user", new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = toolUseId,
                ["content"] = longOutput,
            }),
        });
        return this;
    }

    public TranscriptBuilder AssistantText(string text)
    {
        Add("assistant", new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        });
        return this;
    }

    public TranscriptBuilder RawImageMessage(string marker)
    {
        Add("user", new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = $"screenshot {marker}" },
                new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = "image/png",
                        ["data"] = Convert.ToBase64String(new byte[4096]),
                    },
                }),
        });
        return this;
    }

    public TranscriptBuilder RawLine(string line)
    {
        _lines.Add(line);
        return this;
    }

    private void Add(string type, JsonObject message)
    {
        string uuid = NextUuid();
        var record = new JsonObject
        {
            ["type"] = type,
            ["uuid"] = uuid,
            ["parentUuid"] = _lastUuid,
            ["sessionId"] = "test-session",
            ["message"] = message,
        };
        _lastUuid = uuid;
        _lines.Add(record.ToJsonString());
    }

    public string WriteTo(string directory, string name = "test-session.jsonl")
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, string.Join("\n", _lines) + "\n", new UTF8Encoding(false));
        return path;
    }
}
