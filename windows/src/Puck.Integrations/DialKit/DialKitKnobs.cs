using System.Globalization;
using System.Text.Json.Nodes;

namespace Puck.Integrations.DialKit;

/// <summary>What a knob is, as the puck shows it ("kind" in the dial message).</summary>
public enum KnobKind { Num, Bool, Action, Select, Color, Text }

/// <summary>
/// One thing the puck can turn: a DialKit control, or one part of one (a spring's stiffness, a pad's
/// x). <see cref="SubKey"/> names the part: a spring/easing key, or "x"/"y" for a pad.
/// </summary>
public sealed record Knob(
    string PanelId,
    string PanelName,
    string Path,
    string Label,
    KnobKind Kind,
    double Min,
    double Max,
    double Step,
    string? SubKey = null,
    string Unit = "",
    IReadOnlyList<KnobOption>? Options = null)
{
    /// <summary>Identifies the knob across state updates.</summary>
    public string Key => $"{PanelId}\u0001{Path}\u0001{SubKey}";
}

public sealed record KnobOption(string Value, string Label);

/// <summary>A message for the page, in dialkit-watt's terms ({"type":"set"|"action", panel, path, value}).</summary>
public sealed record PageCommand(JsonObject Message);

/// <summary>
/// DialKit's panels, flattened into knobs, with their current values. Built from the
/// <c>{"type":"state","panels":[{id,name,controls,values}]}</c> message dialkit-watt sends
/// (DialKit's ControlMeta tree and its flat path → value map).
/// </summary>
public sealed class DialKitKnobs
{
    readonly Dictionary<string, JsonObject> _values = [];  // panel id → values (path → value)

    public IReadOnlyList<Knob> Knobs { get; private set; } = [];

    // DialKit's own ranges for the parts of a transition (its spring/easing editor)
    static readonly (string Key, string Label, double Min, double Max, double Step, double Default, string Unit)[] SimpleSpring =
        [("visualDuration", "Duration", 0.1, 1, 0.05, 0.3, "s"), ("bounce", "Bounce", 0, 1, 0.05, 0.2, "")];
    static readonly (string Key, string Label, double Min, double Max, double Step, double Default, string Unit)[] AdvancedSpring =
        [("stiffness", "Stiffness", 1, 1000, 10, 400, ""), ("damping", "Damping", 1, 100, 1, 17, ""), ("mass", "Mass", 0.1, 10, 0.1, 1, "")];
    static readonly (string Key, string Label, double Min, double Max, double Step, double Default, string Unit)[] Easing =
        [("duration", "Duration", 0.1, 2, 0.05, 0.3, "s")];

    /// <summary>Replaces everything with the page's latest panels.</summary>
    public void Load(JsonArray? panels)
    {
        var knobs = new List<Knob>();
        _values.Clear();
        foreach (var panel in (panels ?? []).OfType<JsonObject>())
        {
            var id = Str(panel["id"]) ?? "";
            var name = Str(panel["name"]) ?? id;
            var values = panel["values"] as JsonObject ?? new JsonObject();
            _values[id] = (JsonObject)values.DeepClone();
            Visit(panel["controls"] as JsonArray, id, name, "", knobs);
        }
        Knobs = knobs;
    }

    void Visit(JsonArray? controls, string panelId, string panelName, string folder, List<Knob> into)
    {
        foreach (var c in (controls ?? []).OfType<JsonObject>())
        {
            var type = Str(c["type"]) ?? "";
            var path = Str(c["path"]) ?? "";
            var label = Str(c["label"]) ?? path;
            var full = folder.Length > 0 ? $"{folder} · {label}" : label;
            switch (type)
            {
                case "folder":
                    Visit(c["children"] as JsonArray, panelId, panelName, full, into);
                    break;
                case "slider":
                    {
                        var min = Num(c["min"]) ?? 0;
                        var max = Num(c["max"]) ?? 1;
                        var step = Num(c["step"]) is double s && s > 0 ? s : InferStep(min, max);
                        into.Add(new Knob(panelId, panelName, path, full, KnobKind.Num, min, max, step));
                        break;
                    }
                case "toggle":
                    into.Add(new Knob(panelId, panelName, path, full, KnobKind.Bool, 0, 1, 1));
                    break;
                case "action":
                    into.Add(new Knob(panelId, panelName, path, full, KnobKind.Action, 0, 0, 0));
                    break;
                case "select":
                case "image":
                    {
                        var options = (c["options"] as JsonArray ?? []).Select(ParseOption).OfType<KnobOption>().ToList();
                        if (options.Count > 0)
                            into.Add(new Knob(panelId, panelName, path, full, KnobKind.Select, 0, options.Count - 1, 1, Options: options));
                        break;
                    }
                case "color":
                    into.Add(new Knob(panelId, panelName, path, full, KnobKind.Color, 0, 0, 0));
                    break;
                case "text":
                    into.Add(new Knob(panelId, panelName, path, full, KnobKind.Text, 0, 0, 0));
                    break;
                case "spring":
                case "transition":
                    foreach (var part in TransitionParts(panelId, path))
                        into.Add(new Knob(panelId, panelName, path, $"{full} · {part.Label}", KnobKind.Num, part.Min, part.Max, part.Step, part.Key, part.Unit));
                    break;
                case "pad":
                    {
                        var pad = c["pad"] as JsonObject;
                        var x = PadAxis(pad?["x"]);
                        var y = PadAxis(pad?["y"]);
                        var xl = Str(pad?["labels"]?["x"]) ?? "X";
                        var yl = Str(pad?["labels"]?["y"]) ?? "Y";
                        into.Add(new Knob(panelId, panelName, path, $"{full} · {xl}", KnobKind.Num, x.Min, x.Max, x.Step, "x"));
                        into.Add(new Knob(panelId, panelName, path, $"{full} · {yl}", KnobKind.Num, y.Min, y.Max, y.Step, "y"));
                        break;
                    }
            }
        }
    }

    /// <summary>The editable parts of a transition, by its mode ("simple" / "advanced" / "easing").</summary>
    IEnumerable<(string Key, string Label, double Min, double Max, double Step, double Default, string Unit)> TransitionParts(string panelId, string path) =>
        TransitionMode(panelId, path) switch
        {
            "easing" => Easing,
            "advanced" => AdvancedSpring,
            _ => SimpleSpring,
        };

    string TransitionMode(string panelId, string path)
    {
        var values = _values.GetValueOrDefault(panelId);
        if (Str(values?[$"{path}.__mode"]) is { } mode) return mode;
        var v = values?[path] as JsonObject;
        if (Str(v?["type"]) == "easing") return "easing";
        var physics = v?["stiffness"] is not null || v?["damping"] is not null || v?["mass"] is not null;
        var time = v?["visualDuration"] is not null || v?["bounce"] is not null;
        return physics && !time ? "advanced" : "simple";
    }

    // ---------------------------------------------------------------- values

    public JsonNode? RawValue(Knob k) => _values.GetValueOrDefault(k.PanelId)?[k.Path];

    /// <summary>The knob's value as a number (bools 0/1, selects the option index).</summary>
    public double NumberValue(Knob k)
    {
        var raw = RawValue(k);
        switch (k.Kind)
        {
            case KnobKind.Bool:
                return raw is JsonValue bv && bv.TryGetValue(out bool b) && b ? 1 : 0;
            case KnobKind.Select:
                {
                    var s = Str(raw);
                    var i = k.Options!.ToList().FindIndex(o => o.Value == s);
                    return Math.Max(0, i);
                }
            case KnobKind.Num when k.SubKey is not null:
                {
                    var n = Num((raw as JsonObject)?[k.SubKey]);
                    if (n is not null) return n.Value;
                    var part = SimpleSpring.Concat(AdvancedSpring).Concat(Easing).FirstOrDefault(p => p.Key == k.SubKey);
                    return part.Key is null ? k.Min : part.Default;
                }
            case KnobKind.Num:
                return Num(raw) ?? k.Min;
            default:
                return 0;
        }
    }

    public bool BoolValue(Knob k) => RawValue(k) is JsonValue v && v.TryGetValue(out bool b) && b;

    /// <summary>What the puck prints for the value.</summary>
    public string DisplayText(Knob k) => k.Kind switch
    {
        KnobKind.Num => FormatNumber(NumberValue(k), k.Step) + k.Unit,
        KnobKind.Bool => BoolValue(k) ? "On" : "Off",
        KnobKind.Action => "Run",
        KnobKind.Select => k.Options![(int)NumberValue(k)].Label,
        KnobKind.Color or KnobKind.Text => Common.TextUtil.Truncate(Str(RawValue(k)) ?? "", 40),
        _ => "",
    };

    // ---------------------------------------------------------------- input

    /// <summary>Turning by <paramref name="detents"/>: the new value, set locally, and the message for the page (null if nothing changes).</summary>
    public PageCommand? Turn(Knob k, int detents)
    {
        if (detents == 0) return null;
        switch (k.Kind)
        {
            case KnobKind.Num:
                {
                    var step = k.Step > 0 ? k.Step : (k.Max - k.Min) / 100;
                    if (step <= 0) step = 1;
                    var current = NumberValue(k);
                    var next = Snap(Math.Clamp(current + detents * step, k.Min, k.Max), k.Min, k.Max, step);
                    if (next == current) return null;
                    return SetNumber(k, next);
                }
            case KnobKind.Bool:
                {
                    var want = detents > 0;
                    return BoolValue(k) == want ? null : Set(k, JsonValue.Create(want));
                }
            case KnobKind.Select:
                {
                    var count = k.Options!.Count;
                    var i = (int)NumberValue(k);
                    var next = Math.Clamp(i + detents, 0, count - 1);
                    return next == i ? null : Set(k, JsonValue.Create(k.Options[next].Value));
                }
            default:
                return null;
        }
    }

    /// <summary>A tap: flips a toggle, runs an action, or moves a select to its next option.</summary>
    public PageCommand? Tap(Knob k)
    {
        switch (k.Kind)
        {
            case KnobKind.Bool:
                return Set(k, JsonValue.Create(!BoolValue(k)));
            case KnobKind.Action:
                return new PageCommand(new JsonObject { ["type"] = "action", ["panel"] = k.PanelId, ["path"] = k.Path });
            case KnobKind.Select:
                {
                    var next = ((int)NumberValue(k) + 1) % k.Options!.Count;
                    return Set(k, JsonValue.Create(k.Options[next].Value));
                }
            default:
                return null;
        }
    }

    PageCommand SetNumber(Knob k, double value)
    {
        if (k.SubKey is null) return Set(k, JsonValue.Create(value));

        var current = RawValue(k) as JsonObject;
        JsonObject next;
        if (k.SubKey is "x" or "y")
        {
            // a pad: the whole {x, y} point goes back
            next = new JsonObject
            {
                ["x"] = Num(current?["x"]) ?? 0,
                ["y"] = Num(current?["y"]) ?? 0,
            };
        }
        else
        {
            // a transition: as DialKit's own editor does it, a simple spring drops the physics keys
            // and an advanced one drops the time keys
            next = current is null ? new JsonObject { ["type"] = k.SubKey == "duration" ? "easing" : "spring" } : (JsonObject)current.DeepClone();
            var mode = TransitionMode(k.PanelId, k.Path);
            if (mode == "simple") { next.Remove("stiffness"); next.Remove("damping"); next.Remove("mass"); }
            if (mode == "advanced") { next.Remove("visualDuration"); next.Remove("bounce"); }
        }
        next[k.SubKey] = value;
        return Set(k, next);
    }

    PageCommand Set(Knob k, JsonNode value)
    {
        if (!_values.TryGetValue(k.PanelId, out var values))
        {
            values = new JsonObject();
            _values[k.PanelId] = values;
        }
        values[k.Path] = value.DeepClone();
        return new PageCommand(new JsonObject { ["type"] = "set", ["panel"] = k.PanelId, ["path"] = k.Path, ["value"] = value });
    }

    // ---------------------------------------------------------------- helpers

    static double Snap(double v, double min, double max, double step)
    {
        var snapped = min + Math.Round((v - min) / step) * step;
        return Math.Round(Math.Clamp(snapped, min, max), Decimals(step));
    }

    static int Decimals(double step)
    {
        if (step <= 0 || step >= 1) return 0;
        return Math.Clamp((int)Math.Ceiling(-Math.Log10(step) - 1e-9), 0, 6);
    }

    public static string FormatNumber(double v, double step) =>
        v.ToString("F" + Decimals(step), CultureInfo.InvariantCulture);

    // DialKit's inferStep for [default, min, max] sliders without one
    static double InferStep(double min, double max)
    {
        var range = max - min;
        if (range <= 1) return 0.01;
        if (range <= 10) return 0.1;
        if (range <= 100) return 1;
        return 10;
    }

    static (double Min, double Max, double Step) PadAxis(JsonNode? axis)
    {
        // [default, min, max, step?], defaulting to [0, -1, 1]; with no step DialKit's pad moves in
        // 1/200ths of the range (dial-pad.ts), so a detent here does the same
        if (axis is JsonArray a && a.Count >= 3 && Num(a[1]) is { } min && Num(a[2]) is { } max)
            return (min, max, a.Count > 3 && Num(a[3]) is double s && s > 0 ? s : (max > min ? (max - min) / 200 : 0.01));
        return (-1, 1, 0.01);
    }

    static KnobOption? ParseOption(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue(out string? s) => new KnobOption(s, s),
        JsonObject o when Str(o["value"]) is { } value => new KnobOption(value, Str(o["label"]) ?? value),
        _ => null,
    };

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        return null;
    }
}
