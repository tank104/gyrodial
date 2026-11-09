namespace Puck.Integrations.Common;

static class TextUtil
{
    /// <summary>
    /// At most <paramref name="maxBytes"/> UTF-8 bytes (the puck's limits are bytes, not characters),
    /// cut at a word boundary with an ellipsis when it had to be cut. Whitespace runs collapse to
    /// single spaces (the puck's screen has no room for layout).
    /// </summary>
    public static string Truncate(string? text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var fit = Puck.Core.PuckMessage.FitUtf8(s, maxBytes);
        return fit.Length == s.Length ? fit : fit[..^1].TrimEnd(' ', ',', ';', ':', '-') + "…";
    }
}
