using System.Text;
using System.Text.RegularExpressions;

namespace MVPWorkshop.Core.Formats.Eagl;

/// <summary>
/// Readable names for ANIMS.BIG clips and banks. Clip names follow the artists' convention
/// (<c>prefix_words[_e|_r]</c>, e.g. <c>f_runthrowofhi135r_e</c>): the prefix names the role, the words are run
/// together, and a trailing angle + l/r gives the turn direction. Each clip is stored twice: <c>_e</c> (body)
/// and <c>_r</c> (root/hips). Bank names (DEF_BANK, SIGB0012, NHR1ST01…) are listed in BANKLIST.axt.
/// </summary>
public static partial class EaglAnimNames
{
    private static readonly (string Prefix, string Category)[] Prefixes =
    [
        ("crwd", "Crowd"), ("fa", "Facial"), ("sb", "Scoreboard / broadcast"), ("bc", "Base coach"),
        ("sd", "Dugout"), ("sp", "Pitcher scene"), ("su", "Umpire scene"), ("s", "Scene"), ("n", "Cutscene (NIS)"),
        ("b", "Batting"), ("f", "Fielding"), ("r", "Running"), ("p", "Pitching"), ("c", "Catcher"),
        ("u", "Umpire"), ("h", "Hands"),
    ];

    /// <summary>Order categories appear in the animation list.</summary>
    public static readonly string[] CategoryOrder =
        ["Batting", "Pitching", "Catcher", "Fielding", "Running", "Umpire", "Base coach", "Hands", "Facial",
         "Dugout", "Pitcher scene", "Umpire scene", "Scene", "Cutscene (NIS)", "Scoreboard / broadcast", "Crowd", "Other"];

    public static string Category(string clip)
    {
        var us = clip.IndexOf('_');
        if (us <= 0) return "Other";
        var p = clip[..us];
        foreach (var (prefix, cat) in Prefixes) if (p == prefix) return cat;
        return "Other";
    }

    /// <summary>Clip name without the _e/_r half suffix.</summary>
    public static string ClipName(string anim) =>
        anim.Length > 2 && anim[^2] == '_' && anim[^1] is 'e' or 'r' ? anim[..^2] : anim;

    /// <summary>"f_runthrowofhi135r" → "Run throw OF high 135° R".</summary>
    public static string Title(string clip)
    {
        var us = clip.IndexOf('_');
        var body = us > 0 && Category(clip) != "Other" ? clip[(us + 1)..] : clip;
        // batting stances: <family><c|n|o><nn> = closed / neutral / open stance variant ("wideo01" → wide open 01)
        body = Stance().Replace(body, m => $"{m.Groups[1].Value}_{m.Groups[2].Value switch { "c" => "closed", "n" => "neutral", _ => "open" }}stance{m.Groups[3].Value}_");
        // ordinals stay whole ("3rd_out")
        body = Ordinal().Replace(body, "$1$2_");
        var words = new List<string>();
        foreach (var chunk in body.Split('_'))
            if (chunk.Length > 0) words.AddRange(Segment(chunk));
        if (words.Count == 0) return clip;
        var s = string.Join(' ', words);
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    [GeneratedRegex(@"^(back|upright|wide|generic)([cno])(\d\d)")]
    private static partial Regex Stance();

    [GeneratedRegex(@"(?<![a-z\d])(\d)(st|nd|rd|th)_?")]
    private static partial Regex Ordinal();

    [GeneratedRegex(@"(\d+(?:st|nd|rd|th)(?=_|$)|\d+)")]
    private static partial Regex Digits();

    private static IEnumerable<string> Segment(string chunk)
    {
        // numbers split the run: an angle (45/90/135/180…) eats a following l/r ("135ltostand" → 135° L, to stand);
        // a number at the very end is a variant ("idle03" → #03)
        var pieces = Digits().Split(chunk);
        for (var i = 0; i < pieces.Length; i++)
        {
            var p = pieces[i];
            if (i % 2 == 1 && !char.IsDigit(p[^1])) { yield return p; continue; }
            if (i % 2 == 0) { foreach (var w in Split(p)) yield return w; continue; }
            var next = i + 1 < pieces.Length ? pieces[i + 1] : "";
            if (p is "45" or "68" or "79" or "90" or "135" or "180")
            {
                if (next.Length > 0 && next[0] is 'l' or 'r' && !Words.ContainsKey(next))
                {
                    yield return $"{p}° {char.ToUpperInvariant(next[0])}";
                    pieces[i + 1] = next[1..];
                }
                else yield return $"{p}°";
            }
            else yield return i == pieces.Length - 2 && next.Length <= 1 ? $"#{p}{next.ToUpperInvariant()}" : p;
            if (i == pieces.Length - 2 && next.Length <= 1 && p is not ("45" or "68" or "79" or "90" or "135" or "180")) yield break;
        }
    }

    /// <summary>Dictionary word break (fewest unknown letters, then fewest words); unknown runs are kept as-is.
    /// Single letters (l/r/c = left/right/centre) only count as words at the end of a run.</summary>
    private static List<string> Split(string s)
    {
        var n = s.Length;
        var cost = new (int Unknown, int Words)[n + 1];
        var back = new int[n + 1];
        for (var i = 1; i <= n; i++) cost[i] = (int.MaxValue, 0);
        for (var i = 0; i < n; i++)
        {
            if (cost[i].Unknown == int.MaxValue) continue;
            for (var j = i + 1; j <= n && j - i <= 14; j++)
            {
                var w = s[i..j];
                var known = Words.ContainsKey(w) && (w.Length > 1 || j == n);
                var c = (cost[i].Unknown + (known ? 0 : j - i), cost[i].Words + 1);
                if (c.CompareTo(cost[j]) < 0) { cost[j] = c; back[j] = i; }
            }
        }
        var parts = new List<string>();
        for (var j = n; j > 0; j = back[j]) parts.Add(s[back[j]..j]);
        parts.Reverse();
    // glue consecutive unknown pieces back together
        var result = new List<string>();
        var pending = new StringBuilder();
        foreach (var p in parts)
        {
            if (Words.TryGetValue(p, out var nice) && (p.Length > 1 || ReferenceEquals(p, parts[^1]))) { Flush(); result.Add(nice); }
            else pending.Append(p);
        }
        Flush();
        return result;

        void Flush() { if (pending.Length > 0) { result.Add(pending.ToString()); pending.Clear(); } }
    }

    // word → display form (abbreviations expanded)
    private static readonly Dictionary<string, string> Words = new(StringComparer.Ordinal)
    {
        ["agr"] = "aggressive", ["cas"] = "casual", ["of"] = "OF", ["if"] = "IF", ["ob"] = "OB", ["hi"] = "high", ["lo"] = "low",
        ["med"] = "mid", ["fwd"] = "fwd", ["dbl"] = "double", ["hr"] = "HR", ["hbp"] = "HBP", ["eog"] = "end of game",
        ["eoi"] = "end of inning", ["sog"] = "start of game", ["si"] = "scene intro", ["int"] = "intro", ["nis"] = "NIS",
        ["mgr"] = "manager", ["hmgr"] = "home manager", ["psub"] = "pitching change", ["mvisit"] = "mound visit", ["wohr"] = "walk-off HR",
        ["hifive"] = "high five", ["dughap"] = "dugout happy", ["phap"] = "pitcher happy", ["bupset"] = "batter upset",
        ["bcas"] = "broadcast", ["tocock"] = "to cock", ["pswing"] = "power swing", ["nswing"] = "normal swing",
        ["ih"] = "inside high", ["il"] = "inside low", ["oh"] = "outside high", ["ol"] = "outside low", ["im"] = "inside mid",
        ["om"] = "outside mid",
        ["eject"] = "eject", ["margue"] = "argue", ["io"] = "inning over", ["lk"] = "looking", ["sw"] = "swinging",
        ["l"] = "L", ["r"] = "R", ["c"] = "C", ["closed"] = "closed", ["neutral"] = "neutral", ["open"] = "open", ["stance"] = "stance", ["hfslide"] = "head-first slide", ["ffslide"] = "feet-first slide",
    };

    static EaglAnimNames()
    {
        foreach (var w in """
            about actor after aggr air all and arm arms arrive at avoid away back backhand ball balls bare barehand base bases
            bat batter batting belly big bobble body both brace bunt call casual catch catcher change chat check chewing clap close
            cock contact coach cross crowd cutoff default dirt dive drag drop dugout eject end endswing exag excited extra eye eyec eyeo
            facial fail fast field fielder fielders first flat fly foul foull foulr from fwd generic glove gloveidle grip grnd ground grounder
            happy hand handopen hands hat head high hit hold home homerun hop huddle idle in into inside jog jump kick knee
            left line lineup lefty look looking loop loose low lower mad manager mid miss mis mound mouth moutho move normal
            notalking on ondeck open out outclose outfield over overhand overrun pickoff pinch pitch pitcher play pop popfly power preswing
            protect pull quarter quick r reach react reach ready readyidle reaction right round run runin runner runs safe safeclose scene scoop
            seated set shake sharp short shoulder side sign signal sit situp sitback sitfwd slide slow small spin stand stance steal step stepin
            stepout strafe stretch strike strikelooking strikeout swing swinging tag take takeout talking thigh third throw throwback to
            torso trail turn twitch umpire up upright walk walkback walkin walkup wall warn wave wide widen wiggle wild wind wipe with
            brow gnd closed charge plate celeb center foot shin toss ss let go dug arrive pitcherintro bat bats ws so away
            sit stand standing knee mound visit fist pump cheer bow point tip cap helmet spit adjust gloves dust off bounce
            scoreboard broadcast inning batters runners deck ondeck generic hustle wave catcher catches block blocks frame
            holding letgo lhold rhold upset intro catchgrounder in3rd jogging lob
            """.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
            Words.TryAdd(w, w);
    }

    /// <summary>What a bank holds, from its 8-character name.</summary>
    public static string BankGroup(string bank) => bank switch
    {
        "DEF_BANK" or "DEFBNK_P" or "JUNKBANK" or "MIDDLE" or "MIDDLE_P" or "CLOSE_UP" or "CLSEUP_P" or "EXTR_CLP" => "Gameplay",
        "FACIAL" or "FACIAL_H" or "HANDBANK" => "Face & hands",
        "IGCRWDBK" or "IGCRWD_H" or "NISCRWDS" => "Crowd",
        _ when bank.StartsWith("SIGB", StringComparison.Ordinal) => "Signature batting stances",
        _ when bank.StartsWith("SIGP", StringComparison.Ordinal) => "Signature pitching motions",
        _ when bank.StartsWith('N') => "Cutscenes",
        _ => "Other",
    };

    public static string BankTitle(string bank) => bank switch
    {
        "DEF_BANK" => "Main gameplay set",
        "DEFBNK_P" => "Main gameplay (pitch extras)",
        "JUNKBANK" => "Fielding & running transitions",
        "MIDDLE" => "Mid-range camera set",
        "MIDDLE_P" => "Mid-range camera (pitch extras)",
        "CLOSE_UP" => "Close-up batting set",
        "CLSEUP_P" => "Close-up (pitch extras)",
        "EXTR_CLP" => "Create-a-player",
        "FACIAL" => "Facial expressions",
        "FACIAL_H" => "Facial (create-a-player)",
        "HANDBANK" => "Hand poses",
        "IGCRWDBK" => "In-game crowd",
        "IGCRWD_H" => "In-game crowd (seated)",
        "NISCRWDS" => "Cutscene crowd",
        _ when bank.StartsWith("SIGB", StringComparison.Ordinal) => $"Batting stance {bank[4..]}",
        _ when bank.StartsWith("SIGP", StringComparison.Ordinal) => $"Pitching motion {bank[4..]}",
        _ => bank,
    };
}
