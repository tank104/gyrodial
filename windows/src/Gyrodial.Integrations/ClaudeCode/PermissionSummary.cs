using System.Text.Json;
using System.Text.Json.Nodes;
using Gyrodial.Integrations.Common;

namespace Gyrodial.Integrations.ClaudeCode;

/// <summary>What the dial shows for a Claude Code permission request: the tool, one line of what
/// it wants to do, and the project.</summary>
public sealed record PermissionSummary(string Tool, string Text, string Project)
{
    // UTF-8 bytes: the dial keeps 256 / 40 / 48 (with the terminator)
    public const int MaxText = 240;
    public const int MaxTool = 36;
    public const int MaxProject = 40;

    /// <summary>Summarizes a PermissionRequest hook payload (the JSON Claude Code gives the hook).</summary>
    public static PermissionSummary From(JsonObject request)
    {
        var toolName = Str(request["tool_name"]) ?? "Tool";
        var cwd = Str(request["cwd"]) ?? "";
        var input = request["tool_input"] as JsonObject;

        var text = toolName switch
        {
            "Bash" or "PowerShell" => Str(input?["command"]),
            "Edit" or "Write" or "Read" or "MultiEdit" => Relative(Str(input?["file_path"]), cwd),
            "NotebookEdit" => Relative(Str(input?["notebook_path"]) ?? Str(input?["file_path"]), cwd),
            "WebFetch" => Str(input?["url"]),
            "WebSearch" => Str(input?["query"]),
            "Glob" or "Grep" => Str(input?["pattern"]),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(text) && input is { Count: > 0 })
            text = input.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

        return new PermissionSummary(TextUtil.Truncate(PrettyTool(toolName), MaxTool), TextUtil.Truncate(text, MaxText),
            TextUtil.Truncate(ProjectName(cwd), MaxProject));
    }

    /// <summary>"mcp__github__create_issue" → "github: create_issue".</summary>
    public static string PrettyTool(string toolName)
    {
        if (toolName.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var rest = toolName["mcp__".Length..];
            var split = rest.IndexOf("__", StringComparison.Ordinal);
            if (split > 0) return $"{rest[..split]}: {rest[(split + 2)..]}";
            return rest;
        }
        return toolName;
    }

    static string ProjectName(string cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return "";
        var trimmed = cwd.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    static string? Relative(string? path, string cwd)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(cwd)) return path;
        try
        {
            var rel = Path.GetRelativePath(cwd, path);
            return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? path : rel;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
