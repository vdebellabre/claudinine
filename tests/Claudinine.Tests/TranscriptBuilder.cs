namespace Claudinine.Tests;

/// <summary>Builds synthetic transcript JSONL shaped like the app's records.</summary>
internal sealed class TranscriptBuilder
{
    private readonly List<string> _lines = [];
    private string? _lastUuid;
    private int _seq;

    /// <summary>Uuid of the most recently added chained record.</summary>
    public string? LastUuid => _lastUuid;

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

    /// <summary>
    /// Modern-format (v2.1.222+) tool_use: its OWN assistant record, chained to the
    /// current tail. Emit several in a row to shape a parallel batch.
    /// </summary>
    public TranscriptBuilder ToolUse(string command, out string toolUseId, out string recordUuid)
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
        recordUuid = _lastUuid!;
        return this;
    }

    /// <summary>
    /// Modern-format tool_result: parented to its OWN tool_use record (not the tail)
    /// and carrying sourceToolAssistantUUID, exactly as the app writes batch results.
    /// The tail still advances, so the next record chains off this result.
    /// </summary>
    public TranscriptBuilder ToolResultFor(string toolUseId, string useRecordUuid, string output)
    {
        Add("user", new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = toolUseId,
                ["content"] = output,
            }),
        }, parent: useRecordUuid, sourceToolAssistantUuid: useRecordUuid);
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

    public TranscriptBuilder RawImageMessage(string marker, byte[]? data = null)
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
                        ["data"] = Convert.ToBase64String(data ?? new byte[4096]),
                    },
                }),
        });
        return this;
    }

    /// <summary>User message carrying a base64 document block (pasted PDF).</summary>
    public TranscriptBuilder RawDocumentMessage(string marker, byte[] data,
        string mediaType = "application/pdf")
    {
        Add("user", new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = $"document {marker}" },
                new JsonObject
                {
                    ["type"] = "document",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = mediaType,
                        ["data"] = Convert.ToBase64String(data),
                    },
                }),
        });
        return this;
    }

    /// <summary>
    /// A tool call whose result carries a base64 screenshot nested in its content
    /// array, as browser/computer tools return them.
    /// </summary>
    public TranscriptBuilder ScreenshotToolCall(out string toolUseId, byte[]? data = null)
    {
        toolUseId = $"toolu_{_seq + 1:D4}";
        Add("assistant", new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = toolUseId,
                ["name"] = "computer",
                ["input"] = new JsonObject { ["action"] = "screenshot" },
            }),
        });
        Add("user", new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = toolUseId,
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "text", ["text"] = "screenshot taken" },
                    new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = "image/png",
                            ["data"] = Convert.ToBase64String(data ?? new byte[4096]),
                        },
                    }),
            }),
        });
        return this;
    }

    /// <summary>Uuid-less metadata record (last-prompt, custom-title, mode, ...).</summary>
    public TranscriptBuilder MetaLine(string type, params (string Key, string Value)[] fields)
    {
        var record = new JsonObject { ["type"] = type, ["sessionId"] = "test-session" };
        foreach ((string key, string value) in fields)
            record[key] = value;
        _lines.Add(record.ToJsonString());
        return this;
    }

    public TranscriptBuilder QueueOp(string operation, string? content = null, string session = "test-session")
    {
        var record = new JsonObject
        {
            ["type"] = "queue-operation",
            ["operation"] = operation,
            ["timestamp"] = "2026-08-07T00:00:00.000Z",
            ["sessionId"] = session,
        };
        if (content is not null)
            record["content"] = content;
        _lines.Add(record.ToJsonString());
        return this;
    }

    /// <summary>On-chain system record as the app writes after Stop hooks run.</summary>
    public TranscriptBuilder StopHookSummary(bool hasOutput = false,
        string[]? additionalContext = null, string[]? errors = null,
        bool preventedContinuation = false, string stopReason = "")
    {
        string uuid = NextUuid();
        var record = new JsonObject
        {
            ["parentUuid"] = _lastUuid,
            ["isSidechain"] = false,
            ["type"] = "system",
            ["subtype"] = "stop_hook_summary",
            ["hookCount"] = 2,
            ["hookErrors"] = new JsonArray([.. (errors ?? []).Select(e => (JsonNode)e)]),
            ["hookAdditionalContext"] = new JsonArray([.. (additionalContext ?? []).Select(c => (JsonNode)c)]),
            ["preventedContinuation"] = preventedContinuation,
            ["stopReason"] = stopReason,
            ["hasOutput"] = hasOutput,
            ["level"] = "suggestion",
            ["uuid"] = uuid,
            ["sessionId"] = "test-session",
        };
        _lastUuid = uuid;
        _lines.Add(record.ToJsonString());
        return this;
    }

    /// <summary>
    /// On-chain attachment record as the app writes when a touched file was
    /// modified out-of-band (snippet = the entire current file content).
    /// </summary>
    public TranscriptBuilder Attachment(string attachmentType, params (string Key, string Value)[] fields)
    {
        string uuid = NextUuid();
        var attachment = new JsonObject { ["type"] = attachmentType };
        foreach ((string key, string value) in fields)
            attachment[key] = value;
        var record = new JsonObject
        {
            ["parentUuid"] = _lastUuid,
            ["isSidechain"] = false,
            ["attachment"] = attachment,
            ["type"] = "attachment",
            ["uuid"] = uuid,
            ["sessionId"] = "test-session",
        };
        _lastUuid = uuid;
        _lines.Add(record.ToJsonString());
        return this;
    }

    public TranscriptBuilder EditedTextFile(string filename, string snippet) =>
        Attachment("edited_text_file", ("filename", filename), ("snippet", snippet));

    /// <summary>
    /// Full-snapshot task list reminder as the app writes each turn: content is
    /// the ENTIRE current list (empty array for the zero-state nudge).
    /// </summary>
    public TranscriptBuilder TaskReminder(string? subject = null, bool sidechain = false)
    {
        string uuid = NextUuid();
        var content = new JsonArray();
        if (subject is not null)
        {
            content.Add(new JsonObject
            {
                ["id"] = "1",
                ["subject"] = subject,
                ["description"] = "details",
                ["activeForm"] = "working",
                ["status"] = "pending",
                ["blocks"] = new JsonArray(),
                ["blockedBy"] = new JsonArray(),
            });
        }
        var record = new JsonObject
        {
            ["parentUuid"] = _lastUuid,
            ["isSidechain"] = sidechain,
            ["attachment"] = new JsonObject
            {
                ["type"] = "task_reminder",
                ["content"] = content,
                ["itemCount"] = content.Count,
            },
            ["type"] = "attachment",
            ["uuid"] = uuid,
            ["sessionId"] = "test-session",
        };
        _lastUuid = uuid;
        _lines.Add(record.ToJsonString());
        return this;
    }

    /// <summary>Hook-success attachment as the app writes after a hook runs clean.</summary>
    public TranscriptBuilder HookSuccess(string hookEvent, bool sidechain = false)
    {
        string uuid = NextUuid();
        var record = new JsonObject
        {
            ["parentUuid"] = _lastUuid,
            ["isSidechain"] = sidechain,
            ["attachment"] = new JsonObject
            {
                ["type"] = "hook_success",
                ["hookName"] = hookEvent,
                ["toolUseID"] = NextUuid(),
                ["hookEvent"] = hookEvent,
                ["content"] = "hook output",
                ["stdout"] = "hook output\n",
                ["stderr"] = "",
                ["exitCode"] = 0,
                ["command"] = "some-hook-command",
                ["durationMs"] = 42,
            },
            ["type"] = "attachment",
            ["uuid"] = uuid,
            ["sessionId"] = "test-session",
        };
        _lastUuid = uuid;
        _lines.Add(record.ToJsonString());
        return this;
    }

    /// <summary>
    /// A sequential tool call: use record + result carrier, the carrier bearing
    /// the harness-written toolUseResult object (string for errored calls).
    /// </summary>
    public TranscriptBuilder ToolCall(string name, JsonObject input, string resultText,
        JsonNode? toolUseResult = null)
    {
        string toolUseId = $"toolu_{_seq + 1:D4}";
        Add("assistant", new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = toolUseId,
                ["name"] = name,
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
                ["content"] = resultText,
            }),
        }, toolUseResult: toolUseResult);
        return this;
    }

    public TranscriptBuilder RawLine(string line)
    {
        _lines.Add(line);
        return this;
    }

    private void Add(string type, JsonObject message,
        string? parent = null, string? sourceToolAssistantUuid = null,
        JsonNode? toolUseResult = null)
    {
        string uuid = NextUuid();
        var record = new JsonObject
        {
            ["type"] = type,
            ["uuid"] = uuid,
            ["parentUuid"] = parent ?? _lastUuid,
            ["sessionId"] = "test-session",
            ["message"] = message,
        };
        if (sourceToolAssistantUuid is not null)
            record["sourceToolAssistantUUID"] = sourceToolAssistantUuid;
        if (toolUseResult is not null)
            record["toolUseResult"] = toolUseResult;
        _lastUuid = uuid;
        _lines.Add(record.ToJsonString());
    }

    public string WriteTo(string directory, string name = "test-session.jsonl",
        string newline = "\n", bool trailingNewline = true)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllText(path,
            string.Join(newline, _lines) + (trailingNewline ? newline : ""),
            new UTF8Encoding(false));
        return path;
    }
}
