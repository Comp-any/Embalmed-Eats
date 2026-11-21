using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;

namespace EmbalmedEats.Doctor;

public enum Level { Ok, Info, Warning, Problem }

/// <summary>
/// One card for Vintage Doctor's check-up (or for /embalmedeats check): what was found, the details, what to do,
/// and optionally a fix button and a link to another page.
/// </summary>
public class Finding
{
    /// <summary>The part after "embalmedeats:" in the doctor's results tree. Unique among one set of findings.</summary>
    public string Key;
    public Level Level;
    public string Title;
    public readonly List<string> Details = new();
    public string? Advice;
    /// <summary>A fix of ours (<see cref="DoctorFixes"/>): its full id ("embalmedeats:default") and argument.</summary>
    public string? Fix, FixArgument, FixLabel;
    public string? Link, LinkArgument, LinkLabel;

    public Finding(string key, Level level, string title)
    {
        Key = key;
        Level = level;
        Title = title;
    }

    public Finding Detail(string line)
    {
        if (!string.IsNullOrWhiteSpace(line)) Details.Add(line.Replace("\r", "").Replace("\n", " "));
        return this;
    }

    public Finding WithAdvice(string? advice)
    {
        Advice = advice;
        return this;
    }

    public Finding WithFix(string fixName, string argument, string label)
    {
        Fix = DoctorFixes.Id(fixName);
        FixArgument = argument;
        FixLabel = label;
        return this;
    }

    public Finding WithLink(string page, string argument, string label)
    {
        Link = page;
        LinkArgument = argument;
        LinkLabel = label;
        return this;
    }

    public static string Mark(Level level) => level switch
    {
        Level.Ok => "✓",
        Level.Info => "•",
        Level.Warning => "⚠",
        _ => "✖"
    };

    public static string LevelName(Level level) => level switch
    {
        Level.Ok => "ok",
        Level.Info => "info",
        Level.Warning => "warning",
        _ => "problem"
    };

    /// <summary>Adds this finding to the "results" tree Vintage Doctor sends with "vintagedoctor:collect" or "vintagedoctor:about".</summary>
    public void WriteTo(ITreeAttribute results)
    {
        ITreeAttribute r = results.GetOrAddTreeAttribute(EmbalmedEatsModSystem.ModId + ":" + Key);
        r.SetString("level", LevelName(Level));
        r.SetString("title", Title);
        if (Details.Count > 0) r.SetString("details", string.Join("\n", Details.Take(40)));
        if (!string.IsNullOrEmpty(Advice)) r.SetString("advice", Advice);
        if (Fix != null)
        {
            r.SetString("fix", Fix);
            r.SetString("fixArgument", FixArgument ?? "");
            if (FixLabel != null) r.SetString("fixLabel", FixLabel);
        }
        if (Link != null)
        {
            r.SetString("link", Link);
            r.SetString("linkArgument", LinkArgument ?? "");
            if (LinkLabel != null) r.SetString("linkLabel", LinkLabel);
        }
    }

    /// <summary>The findings as plain text, for /embalmedeats check (fix buttons become the command that does the same).</summary>
    public static string PlainText(IEnumerable<Finding> findings, string lang)
    {
        var sb = new StringBuilder();
        foreach (Finding f in findings)
        {
            sb.Append(Mark(f.Level)).Append(' ').AppendLine(f.Title);
            foreach (string d in f.Details) sb.Append("    ").AppendLine(d);
            if (!string.IsNullOrEmpty(f.Advice)) sb.Append("    → ").AppendLine(f.Advice);
            if (f.Fix != null)
            {
                string command = "/embalmedeats fix " + DoctorFixes.Name(f.Fix) + (string.IsNullOrEmpty(f.FixArgument) ? "" : " " + f.FixArgument);
                sb.Append("    ").AppendLine(Lang.GetL(lang, "embalmedeats:doctor-plain-fix", f.FixLabel ?? "", command));
            }
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// A page for Vintage Doctor's window, written line by line (its "vintagedoctor:page" protocol): each line has a
/// style (0 text, 1 heading, 2 result, 3 note, 4 warning, 5 card) and an indent.
/// </summary>
public class PageLines
{
    public readonly List<(string text, byte style, byte indent)> Lines = new();

    public void Heading(string text) => Lines.Add((text, 1, 0));
    public void Text(string text, int indent = 0) => Lines.Add((text, 0, (byte)indent));
    public void Note(string text, int indent = 0) => Lines.Add((text, 3, (byte)indent));
    public void Warn(string text, int indent = 0) => Lines.Add((text, 4, (byte)indent));

    /// <summary>A card: its mark and title, then its details, its advice and its buttons, indented under it.</summary>
    public void Card(Finding f)
    {
        Lines.Add((Finding.Mark(f.Level) + " " + f.Title, 5, 0));
        foreach (string d in f.Details) Note(d, 1);
        if (!string.IsNullOrEmpty(f.Advice)) Text("→ " + f.Advice, 1);
        var buttons = new List<string>();
        if (f.Fix != null) buttons.Add(FixToken(f.Fix, f.FixArgument ?? "", f.FixLabel ?? f.Fix));
        if (f.Link != null) buttons.Add(OpenToken(f.Link, f.LinkArgument ?? "", f.LinkLabel ?? f.Link));
        if (buttons.Count > 0) Text(string.Join(" ", buttons), 1);
    }

    /// <summary>A button that runs one of our fixes on the server ("vintagedoctor:fix").</summary>
    public static string FixToken(string fixId, string argument, string label) => "{{fix|" + Clean(fixId) + "|" + Clean(argument) + "|" + Clean(label) + "}}";

    /// <summary>A button to another page: ours ("embalmedeats" with an argument) or one of the doctor's ("mod", "log"...).</summary>
    public static string OpenToken(string page, string argument, string label) => "{{open|" + Clean(page) + "|" + Clean(argument) + "|" + Clean(label) + "}}";

    /// <summary>An item or block shown with its icon and name ([[item:game:bread-spelt-perfect]]).</summary>
    public static string ThingToken(Vintagestory.API.Common.CollectibleObject coll) =>
        "[[" + (coll is Vintagestory.API.Common.Block ? "block:" : "item:") + coll.Code + "]]";

    /// <summary>Labels and arguments can't hold the characters the doctor's buttons are written with.</summary>
    static string Clean(string s) => (s ?? "").Replace("{", "(").Replace("}", ")").Replace("|", "/");

    public void WriteTo(ITreeAttribute tree)
    {
        tree["texts"] = new StringArrayAttribute(Lines.Select(l => l.text).ToArray());
        tree.SetBytes("styles", Lines.Select(l => l.style).ToArray());
        tree.SetBytes("indents", Lines.Select(l => l.indent).ToArray());
    }
}
