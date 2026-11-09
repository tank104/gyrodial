using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.Core;

/// <summary>
/// One line of the puck protocol: a JSON object whose "t" is its type (see docs/PROTOCOL.md).
/// </summary>
public sealed class PuckMessage
{
    // Relaxed escaping: the puck takes at most 1024 bytes a line, and the default encoder turns
    // every non-ASCII character and ' " < > & + into a 6-byte \uXXXX. Lines are never put in HTML.
    static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Type { get; }
    public JsonObject Body { get; }

    PuckMessage(string type, JsonObject body)
    {
        Type = type;
        Body = body;
    }

    /// <summary>A message of this type, with the public properties of <paramref name="fields"/>
    /// (an anonymous object such as <c>new { vol = 42, mute = false }</c>) as its fields.</summary>
    public static PuckMessage Create(string type, object? fields = null)
    {
        var body = fields switch
        {
            null => new JsonObject(),
            JsonObject o => (JsonObject)o.DeepClone(),
            _ => JsonSerializer.SerializeToNode(fields) as JsonObject ?? new JsonObject(),
        };
        body["t"] = type;
        return new PuckMessage(type, body);
    }

    /// <summary>Parses one line; null when it isn't a protocol message (logs, noise, bad JSON).</summary>
    public static PuckMessage? TryParse(string line)
    {
        line = line.Trim();
        if (line.Length < 2 || line[0] != '{') return null;
        try
        {
            if (JsonNode.Parse(line) is not JsonObject body) return null;
            if (body["t"] is not JsonValue tv || !tv.TryGetValue(out string? type) || string.IsNullOrEmpty(type)) return null;
            return new PuckMessage(type, body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The message as one compact JSON line, without the trailing newline.</summary>
    public string ToJson() => Body.ToJsonString(Compact);

    public override string ToString() => ToJson();

    public bool Has(string key) => Body.ContainsKey(key);

    public string? GetString(string key) =>
        Body[key] is JsonValue v && v.TryGetValue(out string? s) ? s : Body[key]?.ToJsonString();

    public double? GetDouble(string key)
    {
        if (Body[key] is not JsonValue v) return null;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out string? s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) return d;
        return null;
    }

    public int? GetInt(string key) => GetDouble(key) is double d ? (int)Math.Round(d) : null;

    public bool? GetBool(string key) =>
        Body[key] is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    /// <summary>
    /// <paramref name="text"/> cut to at most <paramref name="maxBytes"/> UTF-8 bytes (on a word
    /// boundary when one is near, with "…" when cut). Use it for every free-text field sent to the
    /// puck: its limits are in bytes, not characters.
    /// </summary>
    public static string FitUtf8(string? text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var utf8 = System.Text.Encoding.UTF8;
        if (utf8.GetByteCount(text) <= maxBytes) return text;
        const string ellipsis = "…";  // 3 bytes
        int budget = Math.Max(0, maxBytes - 3), bytes = 0, cut = 0;
        for (int i = 0; i < text.Length;)
        {
            int len = char.IsSurrogatePair(text, i) ? 2 : 1;
            int n = utf8.GetByteCount(text.AsSpan(i, len));
            if (bytes + n > budget) break;
            bytes += n;
            i += len;
            cut = i;
        }
        int space = text.LastIndexOf(' ', Math.Max(0, cut - 1), Math.Min(cut, 40));
        if (space > cut / 2) cut = space;
        return text[..cut].TrimEnd() + ellipsis;
    }

    public string GetString(string key, string fallback) => GetString(key) ?? fallback;
    public double GetDouble(string key, double fallback) => GetDouble(key) ?? fallback;
    public int GetInt(string key, int fallback) => GetInt(key) ?? fallback;
    public bool GetBool(string key, bool fallback) => GetBool(key) ?? fallback;
}
