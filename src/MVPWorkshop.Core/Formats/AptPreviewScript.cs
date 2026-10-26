using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace MVPWorkshop.Core.Formats;

/// <summary>Localised UI strings (<c>10_easo.loc</c>, <c>KEY=Value|KEY=Value|…</c>) used by <c>ltxt(KEY)</c> in the online screens.</summary>
public sealed class AptLocale
{
    public Dictionary<string, string> Strings { get; } = new(StringComparer.Ordinal);
    public static AptLocale Parse(byte[] data)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(data); } catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(data); }
        var locale = new AptLocale();
        foreach (var pair in text.Split('|'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0) locale.Strings.TryAdd(pair[..eq].Trim('\r', '\n', ' '), pair[(eq + 1)..].Replace("\\n", "\n"));
        }
        return locale;
    }
    public string? Get(string key) => Strings.GetValueOrDefault(key);
}

/// <summary>
/// Bounded ActionScript preview VM. It executes what a static screen needs: variables and registers, function parameters, locals,
/// objects/arrays, <c>this</c>-relative display properties, branches, interval timers, per-sprite frame actions (<c>stop</c>,
/// <c>gotoAndStop</c>) and <c>ltxt()</c> string lookups. Calls into the game's own services (screen manager, EASO, network…) are not
/// executed; each one is recorded in <see cref="Limitations"/>.
/// </summary>
public sealed class AptPreviewScript
{
    private sealed record Reference(string Path);
    private sealed class JsObject { public Dictionary<string, object?> Props { get; } = new(StringComparer.Ordinal); public List<object?>? Items; public JsObject? Proto; }
    private sealed record Param(int Register, string Name);
    private sealed class Block
    {
        public List<Node> Nodes { get; } = [];
        public Dictionary<int, int> Offsets { get; } = [];
        public int End;
    }
    private sealed record Node(AptInstruction I, Function? Def);
    private sealed record Function(string Name, List<Param> Params, int Flags, int Registers, Block Body, AptFile Movie, AptConstants? Constants)
    {
        /// <summary>Function-object properties; <c>prototype</c> is created on first use.</summary>
        public JsObject Props { get; } = new();
        public JsObject Prototype => Props.Props.TryGetValue("prototype", out var p) && p is JsObject o ? o : (JsObject)(Props.Props["prototype"] = new JsObject());
    }
    private sealed record Interval(Function Function, object? This, double Delay, double Next);

    private readonly Dictionary<string, object?> _variables = new(StringComparer.Ordinal);
    private readonly JsObject _global = new();
    private readonly Dictionary<string, Function> _named = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Interval> _intervals = [];
    private readonly Dictionary<string, int> _stopped = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Frame, bool Play)> _goto = new(StringComparer.Ordinal);
    private readonly HashSet<string> _entered = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _interactiveFrames = [];
    public bool Interactive { get; set; }
    public event Action<string>? Execution;

    public int StepTimeline(string path, AptFile movie, AptConstants? constants, AptCharacter clip, int requested)
    {
        if (!Enabled || clip.Frames.Count == 0) return requested;
        _operations = 0;
        int previous = _interactiveFrames.GetValueOrDefault(path, -1);
        if (_goto.Remove(path, out var jump))
        {
            requested = Math.Clamp(jump.Frame, 0, clip.Frames.Count - 1);
            if (jump.Play) _stopped.Remove(path); else _stopped[path] = requested;
        }
        else if (_stopped.TryGetValue(path, out var stopped) && stopped >= 0) return stopped;
        requested = Math.Clamp(requested, 0, clip.Frames.Count - 1);
        if (previous != requested)
        {
            foreach (var action in clip.Frames[requested].Items.Where(i => i.Type == 1 && i.Code.HasValue))
                RunAction(movie, constants, action.Code!.Value, new Reference(path), requested);
            _interactiveFrames[path] = requested;
        }
        return requested;
    }
    private readonly HashSet<AptFile> _initialized = [];
    private readonly Dictionary<string, Function> _classes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Function> _instanceClass = new(StringComparer.Ordinal);
    private int _nextInterval = 1, _operations, _currentFrame;
    private double _time;
    private AptFile _movie = null!;
    private AptConstants? _constants;
    public AptLocale? Locale { get; set; }
    /// <summary>A movie clip the scripts reach by path before the renderer has visited it.</summary>
    public sealed record SpriteInfo(AptFile Movie, AptConstants? Constants, AptCharacter Sprite, IReadOnlyList<string> Linkage);
    /// <summary>
    /// Resolves a script path (<c>menu/menuTitle</c>) to its clip. In Flash a parent's constructor finds its children already created and
    /// bound to their registered classes; the renderer visits children after the parent, so such clips are instantiated on first reference.
    /// </summary>
    public Func<string, SpriteInfo?>? Locator { get; set; }
    private readonly HashSet<string> _located = new(StringComparer.Ordinal);
    /// <summary>False renders the authored layout only: no InitActions, frame actions or timers run.</summary>
    public bool Enabled { get; set; } = true;
    public Dictionary<string, Dictionary<string, object?>> Properties { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Limitations { get; } = [];
    /// <summary>Strings the scripts looked up through <c>ltxt()</c> (key → text), for the limitations/selection panels.</summary>
    public Dictionary<string, string> Localized { get; } = new(StringComparer.Ordinal);
    public IEnumerable<string> Functions => _named.Keys;
    /// <summary>Linkage names bound to a class by <c>Object.registerClass</c> so far.</summary>
    public IEnumerable<string> RegisteredClasses => _classes.Keys;

    /// <summary>Dispatch only an authored handler; missing handlers never become host service calls.</summary>
    public bool Dispatch(string path, string handler)
    {
        if (!Enabled) return false;
        _operations = 0;
        var target = new Reference(path);
        var method = Member(target, handler);
        if (method is not Function function) return false;
        var previousFrame = _currentFrame;
        _currentFrame = _interactiveFrames.GetValueOrDefault(path, _currentFrame);
        try { Call(function, [], target, 0); }
        finally { _currentFrame = previousFrame; }
        return true;
    }

    public bool HasHandler(string path, string handler) => Member(new Reference(path), handler) is Function;

    public void SetPreviewValue(string path, string property, object? value)
    {
        if (!Properties.TryGetValue(path, out var properties)) Properties[path] = properties = new(StringComparer.Ordinal);
        properties[property] = value;
    }

    // ---- loading -------------------------------------------------------------------------------------------------

    private Block Parse(List<AptInstruction> code, AptFile movie, AptConstants? constants)
    {
        var block = new Block();
        void Fill(Block target, int from, int to, int depth)
        {
            for (int i = from; i < to; i++)
            {
                var ins = code[i];
                if (ins.Mnemonic is "DefineFunction" or "DefineFunction2")
                {
                    int end = i + 1; while (end < to && code[end].Depth > ins.Depth) end++;
                    var body = new Block(); Fill(body, i + 1, end, ins.Depth + 1);
                    target.Offsets[ins.Offset] = target.Nodes.Count; target.Nodes.Add(new Node(ins, MakeFunction(ins, body, movie, constants)));
                    i = end - 1;
                }
                else { target.Offsets[ins.Offset] = target.Nodes.Count; target.Nodes.Add(new Node(ins, null)); }
            }
            target.End = code.Count > 0 ? code[Math.Min(to, code.Count) - 1].Offset + 1 : 0;
        }
        Fill(block, 0, code.Count, 0);
        return block;
    }

    private static readonly Regex DefinitionPattern = new(@"^(.*?)\((.*?)\) · (?:(\d+) registers · flags 0x([0-9A-Fa-f]+) · )?\d+ bytes", RegexOptions.Compiled);
    private static Function MakeFunction(AptInstruction ins, Block body, AptFile movie, AptConstants? constants)
    {
        var m = DefinitionPattern.Match(ins.Operands);
        var name = m.Success ? m.Groups[1].Value : "<anonymous>";
        var parameters = new List<Param>();
        if (m.Success)
            foreach (var part in m.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var colon = part.IndexOf(':');
                if (colon > 0 && part[0] == 'r' && int.TryParse(part[1..colon], out var register)) parameters.Add(new Param(register, part[(colon + 1)..]));
                else parameters.Add(new Param(-1, part));
            }
        int regs = m.Success && m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        int flags = m.Success && m.Groups[4].Success ? int.Parse(m.Groups[4].Value, NumberStyles.HexNumber) : 0;
        return new Function(name == "<anonymous>" ? "" : name, parameters, flags, regs, body, movie, constants);
    }

    private Block Load(AptFile movie, AptConstants? constants, int codeOffset)
    {
        var code = AptBytecode.Disassemble(movie.Data, codeOffset, constants);
        return Parse(code, movie, constants);
    }

    /// <summary>Run the movie's InitActions once (class and helper definitions), then the timeline's actions up to <paramref name="frame"/>.</summary>
    public void Initialize(AptFile movie, AptConstants? constants, AptCharacter timeline, int frame)
    {
        _movie = movie; _constants = constants;
        if (!Enabled) return;
        InitMovie(movie, constants);
        var path = "";
        if (Interactive) _interactiveFrames[path] = frame;
        for (int f = 0; f <= Math.Clamp(frame, 0, Math.Max(0, timeline.Frames.Count - 1)) && timeline.Frames.Count > 0; f++)
            foreach (var action in timeline.Frames[f].Items.Where(i => i.Type == 1 && i.Code.HasValue))
                RunAction(movie, constants, action.Code!.Value, new Reference(path), f);
    }

    /// <summary>
    /// Load a shared module the screens reach through <c>_root.&lt;name&gt;</c> (the game's <c>gEaso</c>): run its InitActions and root actions with
    /// <c>this</c> = <paramref name="path"/> so its classes, <c>Object.registerClass</c> calls, helper functions and <c>style</c> palette exist.
    /// </summary>
    public void LoadEnvironment(AptFile movie, AptConstants? constants, string path)
    {
        if (!Enabled) return;
        _movie = movie; _constants = constants;
        InitMovie(movie, constants);
        var before = new HashSet<string>(_variables.Keys);
        if (movie.Movie.Frames.Count > 0)
            foreach (var action in movie.Movie.Frames[0].Items.Where(i => i.Type == 1 && i.Code.HasValue)) RunAction(movie, constants, action.Code!.Value, new Reference(path), 0);
        if (!Properties.TryGetValue(path, out var properties)) Properties[path] = properties = new(StringComparer.Ordinal);
        foreach (var (name, value) in _variables) if (!before.Contains(name) && value is Function) properties[name] = value;
        // `style` is the palette/ChangeColor holder: the ColorStyles prototype
        if (_global.Props.TryGetValue("ColorStyles", out var styles) && styles is Function colorStyles) properties["style"] = colorStyles.Prototype;
    }

    /// <summary>Execute the InitAction blocks of an (imported) movie once.</summary>
    public void InitMovie(AptFile movie, AptConstants? constants)
    {
        if (!_initialized.Add(movie)) return;
        foreach (var action in movie.Movie.Frames.SelectMany(f => f.Items).Concat(movie.Characters.OfType<AptCharacter>().SelectMany(c => c.Frames).SelectMany(f => f.Items)).Where(i => i.Type == 8 && i.Code.HasValue))
            RunAction(movie, constants, action.Code!.Value, new Reference(""), 0);
    }

    private void RunAction(AptFile movie, AptConstants? constants, int offset, object? self, int frame)
    {
        var previous = (_movie, _constants, _currentFrame);
        try
        {
            (_movie, _constants, _currentFrame) = (movie, constants, frame);
            if (Interactive) Execution?.Invoke($"{(self as Reference)?.Path ?? "root"}: frame {frame}, action @0x{offset:X}");
            Run(Load(movie, constants, offset), 0, self, null, null);
        }
        catch (Exception ex) { Limitations.Add("Action parse: " + ex.Message); }
        finally { (_movie, _constants, _currentFrame) = previous; }
    }

    /// <summary>
    /// Run a sprite instance's frame actions (<c>this</c> = <paramref name="path"/>) from frame 0 up to <paramref name="frame"/>,
    /// honouring <c>stop()</c> and <c>gotoAndStop/Play</c>. Returns the frame the clip actually shows.
    /// </summary>
    public int EnterSprite(string path, AptFile movie, AptConstants? constants, AptCharacter sprite, int frame, IEnumerable<string>? linkage = null)
    {
        if (sprite.Frames.Count == 0) return 0;
        frame = Interactive ? Math.Max(0, frame) % sprite.Frames.Count : Math.Clamp(frame, 0, sprite.Frames.Count - 1);
        if (!Enabled) return frame;
        InitMovie(movie, constants);
        if (Interactive)
        {
            if (_entered.Add(path))
                foreach (var name in linkage ?? [])
                    if (_classes.TryGetValue(name, out var registered) && _instanceClass.TryAdd(path, registered))
                        Call(registered, [], new Reference(path), 0);
            return StepTimeline(path, movie, constants, sprite, frame);
        }
        if (_entered.Add(path))
        {
            // Object.registerClass: a symbol with a registered class gets that class's constructor and prototype methods
            foreach (var name in linkage ?? [])
                if (_classes.TryGetValue(name, out var registered) && _instanceClass.TryAdd(path, registered))
                    Call(registered, [], new Reference(path), 0);
            int shown = 0;
            for (int guard = 0, f = 0; f <= frame && guard < 256; f++, guard++)
            {
                shown = f;
                foreach (var action in sprite.Frames[f].Items.Where(i => i.Type == 1 && i.Code.HasValue)) RunAction(movie, constants, action.Code!.Value, new Reference(path), f);
                if (_goto.Remove(path, out var jump))
                {
                    f = Math.Clamp(jump.Frame, 0, sprite.Frames.Count - 1) - 1; shown = f + 1;
                    if (!jump.Play) { _stopped[path] = shown; break; }
                }
                if (_stopped.ContainsKey(path)) break;
            }
            if (!_stopped.ContainsKey(path)) _stopped[path] = -1;
            if (_stopped[path] < 0) return frame;
            return _stopped[path];
        }
        return _stopped.TryGetValue(path, out var stop) && stop >= 0 ? stop : frame;
    }

    public void Invoke(string name)
    {
        if (Enabled && _named.TryGetValue(name, out var function)) Call(function, function.Params.Count > 0 ? [new Reference("")] : [], new Reference(""), 0);
    }

    public void Advance(double milliseconds)
    {
        if (!Enabled) return;
        _operations = 0;
        var end = Math.Clamp(milliseconds, 0, 60000);
        for (int tick = 0; tick < 2000; tick++)
        {
            var due = _intervals.Where(i => i.Value.Next <= end).OrderBy(i => i.Value.Next).FirstOrDefault();
            if (due.Value is null) { _time = end; return; }
            _time = due.Value.Next;
            _intervals[due.Key] = due.Value with { Next = _time + due.Value.Delay };
            Call(due.Value.Function, [], due.Value.This, 0);
        }
        Limitations.Add("Timer preview limit reached");
    }

    // ---- display-object state read by the renderer ---------------------------------------------------------------

    public double Number(string path, string property, double fallback) => Properties.TryGetValue(path, out var properties) && properties.TryGetValue(property, out var value) ? Numeric(value) : fallback;
    public string Text(string path, string property, string fallback) => Properties.TryGetValue(path, out var properties) && properties.TryGetValue(property, out var value) ? (value is null ? "" : String(value)) : fallback;
    public bool Has(string path, string property) => Properties.TryGetValue(path, out var properties) && properties.ContainsKey(property);
    /// <summary>A clip's script-set colour transform (<c>Color.setTransform/setRGB</c>): multiply (1 = 100%) and add (0–1) per channel.</summary>
    public (Vector4 Multiply, Vector4 Add)? ColorTransform(string path)
    {
        if (!Properties.TryGetValue(path, out var properties) || !properties.TryGetValue("_cxform", out var value) || value is not JsObject t) return null;
        float N(string key, float fallback) => t.Props.TryGetValue(key, out var x) ? (float)Numeric(x) : fallback;
        return (new Vector4(N("ra", 100) / 100, N("ga", 100) / 100, N("ba", 100) / 100, N("aa", 100) / 100), new Vector4(N("rb", 0) / 255, N("gb", 0) / 255, N("bb", 0) / 255, N("ab", 0) / 255));
    }
    public bool Visible(string path) => !Properties.TryGetValue(path, out var properties) || !properties.TryGetValue("_visible", out var value) || Truth(value);

    // ---- value model ---------------------------------------------------------------------------------------------

    private object? Variable(string name, Dictionary<string, object?>? locals)
    {
        if (locals is not null && locals.TryGetValue(name, out var local)) return local;
        if (_variables.TryGetValue(name, out var value)) return value;
        if (_global.Props.TryGetValue(name, out var global)) return global;
        return name switch { "_global" => _global, "this" or "_root" or "_level0" => new Reference(""), _ => new Reference(name) };
    }

    private object? Member(object? instance, string property)
    {
        switch (instance)
        {
            case JsObject o:
                if (property == "length" && o.Items is not null) return (double)o.Items.Count;
                if (o.Items is not null && int.TryParse(property, out var index)) return index >= 0 && index < o.Items.Count ? o.Items[index] : null;
                return Lookup(o, property, out var found) ? found : null;
            case Function f:
                return property == "prototype" ? f.Prototype : f.Props.Props.GetValueOrDefault(property);
            case Reference reference:
                if (Properties.TryGetValue(reference.Path, out var properties) && properties.TryGetValue(property, out var value)) return value;
                Ensure(reference.Path);
                if (ClassPrototype(reference.Path) is { } prototype && Lookup(prototype, property, out var inherited)) return inherited;
                return property switch
                {
                    "_alpha" or "_xscale" or "_yscale" => 100d, "_visible" => true, "_x" or "_y" or "_rotation" => 0d,
                    "_root" or "_parent" => new Reference(""),
                    _ => new Reference((reference.Path.Length == 0 ? "" : reference.Path + "/") + property),
                };
            case string s when property == "length": return (double)s.Length;
            default: return null;
        }
    }

    private static bool Lookup(JsObject? o, string name, out object? value)
    {
        for (int i = 0; o is not null && i < 64; o = o.Proto, i++)
            if (o.Props.TryGetValue(name, out value)) return true;
        value = null; return false;
    }
    /// <summary>Instantiate (frame-0 actions + registered class) a clip the first time a script touches it.</summary>
    private void Ensure(string path)
    {
        if (path.Length == 0 || Locator is null || !_located.Add(path)) return;
        var slash = path.LastIndexOf('/');
        if (slash > 0) Ensure(path[..slash]);
        if (Locator(path) is { } info) EnterSprite(path, info.Movie, info.Constants, info.Sprite, 0, info.Linkage);
    }

    private JsObject? ClassPrototype(string path) => _instanceClass.TryGetValue(path, out var ctor) ? ctor.Prototype : null;

    private void Set(object? instance, string property, object? value)
    {
        switch (instance)
        {
            case Function f:
                f.Props.Props[property] = value;
                break;
            case JsObject when property == "ColorStyles" && value is Function colorStyles && ReferenceEquals(instance, _global):
                // The palette lives in the constructor body, guarded by COLORPALETTE == "ncaa"; later prototype entries read it, so
                // evaluate it once now and publish the result on the prototype.
                _global.Props[property] = value;
                var seed = new JsObject(); seed.Props["COLORPALETTE"] = "ncaa";
                Call(colorStyles, [], seed, 0);
                foreach (var (key, entry) in seed.Props) colorStyles.Prototype.Props.TryAdd(key, entry);
                break;
            case JsObject o:
                if (o.Items is not null && int.TryParse(property, out var index) && index >= 0 && index < 4096) { while (o.Items.Count <= index) o.Items.Add(null); o.Items[index] = value; }
                else o.Props[property] = value;
                if (value is Function { Name.Length: 0 } anonymous) _named.TryAdd(property, anonymous);
                break;
            case Reference reference:
                if (!Properties.TryGetValue(reference.Path, out var properties)) Properties[reference.Path] = properties = new(StringComparer.Ordinal);
                properties[property] = value;
                break;
        }
    }

    private static string String(object? value) => value switch { string s => s, Reference r => r.Path, null => "undefined", bool b => b ? "true" : "false", JsObject => "[object]", Function => "[function]", _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" };
    /// <summary>String concatenation operand: values the game would supply at runtime (undefined here) contribute nothing rather than the word "undefined".</summary>
    private static string Concat(object? value) => value is null ? "" : String(value);
    private static double Numeric(object? value) => value is bool b ? b ? 1 : 0 : value is double d ? d : double.TryParse(String(value).Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
    private static bool Truth(object? value) => value switch { null => false, bool b => b, string s => s.Length > 0, double d => d != 0, _ => true };
    private static object? Literal(string value) => value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2 ? value[1..^1] : value switch { "true" => true, "false" => false, "undefined" or "null" => null, _ => double.TryParse(value.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : value };

    // ---- calls ---------------------------------------------------------------------------------------------------

    private object? Call(Function function, object?[] args, object? self, int depth)
    {
        if (depth > 32) { Limitations.Add("Function recursion limit reached"); return null; }
        var locals = new Dictionary<string, object?>(StringComparer.Ordinal);
        var registers = new object?[Math.Max(function.Registers, 8) + 4];
        // DefineFunction2 preloads, in register order: this, arguments, super, root, parent, global
        int next = 1;
        void Preload(object? value) { if (next < registers.Length) registers[next++] = value; }
        if ((function.Flags & 0x01) != 0) Preload(self);
        if ((function.Flags & 0x04) != 0) Preload(new JsObject { Items = [.. args] });
        if ((function.Flags & 0x10) != 0) Preload(null);
        if ((function.Flags & 0x40) != 0) Preload(new Reference(""));
        if ((function.Flags & 0x80) != 0) Preload(new Reference(""));
        if ((function.Flags & 0x100) != 0) Preload(_global);
        for (int i = 0; i < function.Params.Count; i++)
        {
            var parameter = function.Params[i]; var value = i < args.Length ? args[i] : null;
            if (parameter.Register >= 0 && parameter.Register < registers.Length) registers[parameter.Register] = value; else locals[parameter.Name] = value;
        }
        var saved = (_movie, _constants); (_movie, _constants) = (function.Movie, function.Constants);
        try { return Run(function.Body, depth + 1, self, locals, registers); }
        finally { (_movie, _constants) = saved; }
    }

    private object? CallGlobal(string name, object?[] args, object? self, int depth, Dictionary<string, object?>? locals)
    {
        var target = Variable(name, locals);
        if (target is Function f) return Call(f, args, self, depth);
        if (_named.TryGetValue(name, out var named)) return Call(named, args, self, depth);
        switch (name)
        {
            case "setInterval" when args.Length >= 2:
            {
                var callback = args[0] as Function;
                if (callback is null && _named.TryGetValue(String(args[0]), out var byName)) callback = byName;
                if (callback is null) { Limitations.Add("Unresolved interval callback"); return -1d; }
                var delay = Math.Clamp(Numeric(args[1]), 1, 60000);
                var id = _nextInterval++; _intervals[id] = new Interval(callback, self, delay, _time + delay); return (double)id;
            }
            case "clearInterval" when args.Length > 0: _intervals.Remove((int)Numeric(args[0])); return null;
            case "trace": if (Interactive) Execution?.Invoke("trace: " + string.Join(" ", args.Select(String))); return null;
            case "stop": if (self is Reference stopTarget) _stopped[stopTarget.Path] = _currentFrame; return null;
            case "play": if (self is Reference playTarget) _stopped.Remove(playTarget.Path); return null;
            case "ASSetPropFlags" or "updateAfterEvent": return null;
            case "ltxt" or "getLocalizedText" when args.Length > 0:
            {
                var key = String(args[0]);
                if (Locale?.Get(key) is { } text) { Localized[key] = text; return text; }
                Limitations.Add($"Missing localisation key {key}"); return key;
            }
            case "parseInt" or "parseFloat" or "Number" when args.Length > 0: return Numeric(args[0]);
            case "String" when args.Length > 0: return String(args[0]);
            case "Boolean" when args.Length > 0: return Truth(args[0]);
            case "isNaN" when args.Length > 0: return double.IsNaN(Numeric(args[0]));
        }
        Limitations.Add($"External call: {name}"); return null;
    }

    private object? CallMethod(object? target, string name, object?[] args, int depth)
    {
        switch (target)
        {
            case JsObject { Props: var colorProps } colorObject when colorProps.ContainsKey("__color"):
            {
                var path = (colorProps["__color"] as Reference)?.Path;
                if (path is null) return null;
                if (!Properties.TryGetValue(path, out var clipProperties)) Properties[path] = clipProperties = new(StringComparer.Ordinal);
                switch (name)
                {
                    case "setTransform" when args.Length > 0: clipProperties["_cxform"] = args[0]; return null;
                    case "getTransform": return clipProperties.GetValueOrDefault("_cxform");
                    case "setRGB" when args.Length > 0:
                    {
                        var rgb = (long)Numeric(args[0]); var t = new JsObject();
                        t.Props["ra"] = t.Props["ga"] = t.Props["ba"] = 0d;
                        t.Props["rb"] = (double)((rgb >> 16) & 255); t.Props["gb"] = (double)((rgb >> 8) & 255); t.Props["bb"] = (double)(rgb & 255);
                        clipProperties["_cxform"] = t; return null;
                    }
                }
                break;
            }
            case JsObject o:
                if (Lookup(o, name, out var method) && method is Function f) return Call(f, args, o, depth);
                if (o.Items is not null)
                    switch (name)
                    {
                        case "push": o.Items.AddRange(args); return (double)o.Items.Count;
                        case "pop": { if (o.Items.Count == 0) return null; var last = o.Items[^1]; o.Items.RemoveAt(o.Items.Count - 1); return last; }
                        case "join": return string.Join(args.Length > 0 ? String(args[0]) : ",", o.Items.Select(String));
                    }
                break;
            case Reference { Path: "Object" } when name == "registerClass" && args.Length >= 2:
                if (args[1] is Function ctor) _classes[String(args[0])] = ctor;
                return null;
            case Reference r:
                switch (name)
                {
                    case "stop": _stopped[r.Path] = _currentFrame; return null;
                    case "play": _stopped.Remove(r.Path); return null;
                    case "gotoAndStop" or "gotoAndPlay" when args.Length > 0: _goto[r.Path] = (FrameNumber(args[0]), name == "gotoAndPlay"); return null;
                    case "nextFrame" or "prevFrame": _goto[r.Path] = (Math.Max(0, _interactiveFrames.GetValueOrDefault(r.Path, _currentFrame) + (name == "nextFrame" ? 1 : -1)), false); return null;
                }
                Ensure(r.Path);
                if (Properties.TryGetValue(r.Path, out var props) && props.GetValueOrDefault(name) is Function pf) return Call(pf, args, r, depth);
                if (ClassPrototype(r.Path) is { } classPrototype && Lookup(classPrototype, name, out var classMethod) && classMethod is Function cf) return Call(cf, args, r, depth);
                break;
            case string s:
                switch (name)
                {
                    case "toUpperCase": return s.ToUpperInvariant();
                    case "toLowerCase": return s.ToLowerInvariant();
                    case "substring" or "substr" when args.Length > 0:
                    {
                        int a = Math.Clamp((int)Numeric(args[0]), 0, s.Length), n = args.Length > 1 ? Math.Clamp((int)Numeric(args[1]), 0, s.Length - a) : s.Length - a;
                        return s.Substring(a, n);
                    }
                    case "indexOf" when args.Length > 0: return (double)s.IndexOf(String(args[0]), StringComparison.Ordinal);
                }
                break;
        }
        if (name.Length == 0 && target is null) return null;   // super() on an unresolved base class
        Limitations.Add($"External method: {(target is Reference rr && rr.Path.Length > 0 ? rr.Path : target is JsObject ? "object" : String(target))}.{name}");
        return null;
    }

    private int FrameNumber(object? value)
    {
        if (value is string label)
        {
            // frame labels are resolved against the current movie's timelines
            foreach (var item in _movie.Characters.OfType<AptCharacter>().SelectMany(c => c.Frames).SelectMany(f => f.Items).Where(i => i.Type == 2 && i.Name == label)) return item.LabelFrame;
        }
        return Math.Max(0, (int)Numeric(value) - 1);
    }

    private object? Construct(string className, object?[] args, Dictionary<string, object?>? locals, int depth)
    {
        var result = new JsObject();
        if (className != "Array" && (Variable(className, locals) as Function ?? _named.GetValueOrDefault(className)) is { } declared) result.Proto = declared.Prototype;
        if (className == "Array") { result.Items = args.Length == 1 && args[0] is double n ? [.. new object?[(int)Math.Clamp(n, 0, 4096)]] : [.. args]; return result; }
        if (className == "Object") return result;
        if (className == "Color") { result.Props["__color"] = args.Length > 0 ? args[0] : null; return result; }
        if (Variable(className, locals) is Function constructor) { Call(constructor, args, result, depth); return result; }
        if (_named.TryGetValue(className, out var named)) { Call(named, args, result, depth); return result; }
        Limitations.Add($"External constructor: {className}");
        return result;
    }

    // ---- interpreter ---------------------------------------------------------------------------------------------

    private object? Run(Block block, int depth, object? self, Dictionary<string, object?>? locals, object?[]? registers)
    {
        var stack = new Stack<object?>();
        registers ??= new object?[256];
        object? Pop() => stack.Count > 0 ? stack.Pop() : null;
        object?[] Arguments()
        {
            int count = Math.Clamp((int)Numeric(Pop()), 0, 256);
            var args = new object?[count]; for (int i = 0; i < count; i++) args[i] = Pop(); return args;
        }
        object? Register(string text) => int.TryParse(text.TrimStart('r'), out var r) && r >= 0 && r < registers.Length ? registers[r] : null;
        var nodes = block.Nodes;
        for (int pc = 0; pc < nodes.Count; pc++)
        {
            if (++_operations > 2_000_000) { Limitations.Add("Action execution limit reached"); return null; }
            var node = nodes[pc]; var instruction = node.I; var op = instruction.Mnemonic; var argument = instruction.Operands;
            switch (op)
            {
                case "End": return null;
                case "Return": return Pop();
                case "DefineFunction" or "DefineFunction2":
                {
                    var function = node.Def!;
                    if (function.Name.Length > 0) { _named[function.Name] = function; if (locals is null) _variables[function.Name] = function; else locals[function.Name] = function; }
                    else stack.Push(function);
                    break;
                }
                case "ConstantPool" or "StopSounds" or "ToggleQuality" or "SetTarget" or "SetTarget2": break;
                case "Play": if (self is Reference player) _stopped.Remove(player.Path); break;
                case "NextFrame" or "PrevFrame": if (self is Reference stepper) _goto[stepper.Path] = (Math.Max(0, _currentFrame + (op == "NextFrame" ? 1 : -1)), false); break;
                case "Stop": if (self is Reference stopper) _stopped[stopper.Path] = _currentFrame; break;
                case "GotoFrame": if (self is Reference jumper && int.TryParse(argument, out var frame)) _goto[jumper.Path] = (frame, false); break;
                case "GotoLabel": if (self is Reference labeled) _goto[labeled.Path] = (FrameNumber(String(Literal(argument))), false); break;
                case "GotoFrame2": { var target = Pop(); if (self is Reference r2) _goto[r2.Path] = (FrameNumber(target), argument == "play"); break; }
                case "EA_PushConstant" or "EA_PushByte" or "EA_PushShort" or "EA_PushFloat" or "EA_PushLong" or "EA_PushWordConstant" or "EA_PushString": stack.Push(Literal(argument)); break;
                case "EA_PushZero": stack.Push(0d); break;
                case "EA_PushOne": stack.Push(1d); break;
                case "EA_PushTrue": stack.Push(true); break;
                case "EA_PushFalse": stack.Push(false); break;
                case "EA_PushNull" or "EA_PushUndefined": stack.Push(null); break;
                case "EA_PushThis": stack.Push(self); break;
                case "EA_PushGlobal": stack.Push(_global); break;
                case "EA_PushValueOfVar" or "EA_GetStringVar": stack.Push(Variable(String(Literal(argument)), locals)); break;
                case "GetVariable": stack.Push(Variable(String(Pop()), locals)); break;
                case "DefineLocal": { var value = Pop(); var name = String(Pop()); if (locals is null) _variables[name] = value; else locals[name] = value; break; }
                case "DefineLocal2": { var name = String(Pop()); if (locals is null) _variables[name] = null; else locals[name] = null; break; }
                case "SetVariable": { var value = Pop(); var name = String(Pop()); if (locals is not null && locals.ContainsKey(name)) locals[name] = value; else _variables[name] = value; break; }
                case "EA_SetStringVar": { var name = String(Literal(argument)); var value = Pop(); if (locals is not null && locals.ContainsKey(name)) locals[name] = value; else _variables[name] = value; break; }
                case "GetMember": { var property = String(Pop()); stack.Push(Member(Pop(), property)); break; }
                case "EA_GetNamedMember" or "EA_GetStringMember": stack.Push(Member(Pop(), String(Literal(argument)))); break;
                case "SetMember": { var value = Pop(); var property = String(Pop()); Set(Pop(), property, value); break; }
                case "EA_SetStringMember": { var value = Pop(); var property = String(Literal(argument)); Set(Pop(), property, value); break; }
                case "PushData":
                    foreach (Match match in Regex.Matches(argument, "\"[^\"]*\"|[^,]+"))
                    {
                        var literal = match.Value.Trim();
                        stack.Push(literal.Length > 1 && literal[0] == 'r' && char.IsDigit(literal[1]) ? Register(literal) : Literal(literal));
                    }
                    break;
                case "StoreRegister": { if (int.TryParse(argument.TrimStart('r'), out var r) && r >= 0 && r < registers.Length) registers[r] = stack.TryPeek(out var stored) ? stored : null; break; }
                case "EA_PushRegister": stack.Push(Register(argument)); break;
                case "Trace": { var message = String(Pop()); if (Interactive) Execution?.Invoke("trace: " + message); break; }
                case "ToNumber" or "ToInteger": stack.Push(op == "ToInteger" ? Math.Truncate(Numeric(Pop())) : Numeric(Pop())); break;
                case "ToString": stack.Push(String(Pop())); break;
                case "TypeOf": stack.Push(Pop() switch { null => "undefined", double => "number", string => "string", bool => "boolean", Function => "function", _ => "object" }); break;
                case "StringLength": stack.Push((double)String(Pop()).Length); break;
                case "CallFunction" or "EA_CallFunction" or "EA_CallFunctionPop":
                { var name = String(Pop()); var result = CallGlobal(name, Arguments(), self, depth, locals); if (op != "EA_CallFunctionPop") stack.Push(result); break; }
                case "EA_CallNamedFunction" or "EA_CallNamedFunctionPop":
                { var result = CallGlobal(String(Literal(argument)), Arguments(), self, depth, locals); if (op == "EA_CallNamedFunction") stack.Push(result); break; }
                case "CallMethod" or "EA_CallMethod" or "EA_CallMethodPop":
                {
                    var name = String(Pop()); var target = Pop(); var result = name.Length == 0 && target is Function fn ? Call(fn, Arguments(), self, depth) : CallMethod(target, name, Arguments(), depth);
                    if (op != "EA_CallMethodPop") stack.Push(result); break;
                }
                case "EA_CallNamedMethod" or "EA_CallNamedMethodPop":
                {
                    var target = Pop(); var result = CallMethod(target, String(Literal(argument)), Arguments(), depth);
                    if (op == "EA_CallNamedMethod") stack.Push(result); break;
                }
                case "NewObject": { var className = String(Pop()); stack.Push(Construct(className, Arguments(), locals, depth)); break; }
                case "InitArray": { var items = Arguments(); stack.Push(new JsObject { Items = [.. items] }); break; }
                case "InitObject":
                {
                    int count = Math.Clamp((int)Numeric(Pop()), 0, 256); var o = new JsObject();
                    for (int i = 0; i < count; i++) { var value = Pop(); o.Props[String(Pop())] = value; }
                    stack.Push(o); break;
                }
                case "Extends":
                {
                    var superclass = Pop() as Function; var subclass = Pop() as Function;
                    if (superclass is not null && subclass is not null) subclass.Props.Props["prototype"] = new JsObject { Proto = superclass.Prototype };
                    break;
                }
                case "ImplementsOp": { Pop(); int count = Math.Clamp((int)Numeric(Pop()), 0, 64); for (int i = 0; i < count; i++) Pop(); break; }
                case "Delete" or "Delete2": Pop(); if (op == "Delete") Pop(); stack.Push(true); break;
                case "Pop": Pop(); break;
                case "PushDuplicate": stack.Push(stack.TryPeek(out var top) ? top : null); break;
                case "StackSwap": { var right = Pop(); var left = Pop(); stack.Push(right); stack.Push(left); break; }
                case "Not": stack.Push(!Truth(Pop())); break;
                case "Increment": stack.Push(Numeric(Pop()) + 1); break;
                case "Decrement": stack.Push(Numeric(Pop()) - 1); break;
                case "Add" or "Add2" or "StringAdd" or "Subtract" or "Multiply" or "Divide" or "Modulo" or "Equals" or "Equals2" or "StrictEquals" or "StringEquals" or "Less" or "Less2" or "Greater" or "And" or "Or"
                    or "BitAnd" or "BitOr" or "BitXor" or "BitLShift" or "BitRShift" or "BitURShift":
                {
                    var right = Pop(); var left = Pop(); double a = Numeric(left), b = Numeric(right);
                    bool strings = left is string || right is string;
                    stack.Push(op switch
                    {
                        "Add" or "Add2" when strings => Concat(left) + Concat(right), "Add" or "Add2" => a + b, "StringAdd" => Concat(left) + Concat(right),
                        "Subtract" => a - b, "Multiply" => a * b, "Divide" => b == 0 ? 0d : a / b, "Modulo" => b == 0 ? 0d : a % b,
                        "Less" or "Less2" => a < b, "Greater" => a > b, "And" => Truth(left) && Truth(right), "Or" => Truth(left) || Truth(right),
                        "BitAnd" => (double)((long)a & (long)b), "BitOr" => (double)((long)a | (long)b), "BitXor" => (double)((long)a ^ (long)b),
                        "BitLShift" => (double)((int)a << ((int)b & 31)), "BitRShift" => (double)((int)a >> ((int)b & 31)), "BitURShift" => (double)((uint)(int)a >> ((int)b & 31)),
                        "StrictEquals" => left is null || right is null ? left is null && right is null : left.GetType() == right.GetType() && (left is string ? String(left) == String(right) : left is double ? a == b : Equals(left, right)),
                        _ => left is null || right is null ? left is null && right is null : left is Reference or JsObject or Function || right is Reference or JsObject or Function ? Equals(left, right) : strings ? String(left) == String(right) : a == b,
                    });
                    break;
                }
                case "BranchAlways" or "BranchIfTrue" or "EA_BranchIfFalse":
                {
                    var take = op == "BranchAlways" || (op == "BranchIfTrue" ? Truth(Pop()) : !Truth(Pop()));
                    var hex = argument.LastIndexOf("0x", StringComparison.Ordinal);
                    if (take && hex >= 0 && int.TryParse(argument[(hex + 2)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var target))
                    {
                        if (block.Offsets.TryGetValue(target, out var destination)) pc = destination - 1;
                        else if (target >= block.End) return null;
                        else { Limitations.Add($"Unresolved branch target 0x{target:X}"); return null; }
                    }
                    break;
                }
                default: Limitations.Add($"Unsupported action: {op}"); stack.Clear(); break;
            }
        }
        return null;
    }
}
