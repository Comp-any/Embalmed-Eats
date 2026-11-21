using System;
using System.IO;
using System.Linq;
using System.Reflection;
using EmbalmedEats.Config;
using EmbalmedEats.Core;
using EmbalmedEats.Toxicity;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace EmbalmedEats.Doctor;

/// <summary>
/// What the fix buttons do (Vintage Doctor's "vintagedoctor:fix", or /embalmedeats fix without the doctor). All on
/// the server. Nothing is deleted: before the settings file is changed, the old one is copied to
/// ModData/embalmedeats/backups/&lt;date and time&gt;/.
/// </summary>
public static class DoctorFixes
{
    public const string Default = "default";            // argument: a setting's name
    public const string MethodsOn = "methods-on";        // switch all four methods on
    public const string DropUnmatched = "drop-unmatched"; // take mistyped entries out of the food lists
    public const string Cure = "cure";                   // argument: a player's uid, or "all"

    public static readonly string[] All = { Default, MethodsOn, DropUnmatched, Cure };

    public static string Id(string name) => EmbalmedEatsModSystem.ModId + ":" + name;

    public static string Name(string id) => id.StartsWith(EmbalmedEatsModSystem.ModId + ":", StringComparison.Ordinal) ? id.Substring(EmbalmedEatsModSystem.ModId.Length + 1) : id;

    static string T(string lang, string key, params object[] args) => DoctorText.T(lang, key, args);

    /// <summary>Settings that only take full effect at the next start (they decide recipes, food and ground storage).</summary>
    static readonly string[] NeedRestart =
    {
        nameof(EmbalmedEatsConfig.EnableWaxDipping), nameof(EmbalmedEatsConfig.EnableResinLacquer),
        nameof(EmbalmedEatsConfig.WaxItems), nameof(EmbalmedEatsConfig.LacquerItems), nameof(EmbalmedEatsConfig.PreservativePerFood),
        nameof(EmbalmedEatsConfig.AlwaysPreservable), nameof(EmbalmedEatsConfig.NeverPreservable), nameof(EmbalmedEatsConfig.AllowOnGround)
    };

    /// <summary>Does a fix. Returns one line saying what happened, and whether it didn't work.</summary>
    public static (string result, bool failed) Run(ICoreServerAPI sapi, string name, string argument, string lang)
    {
        try
        {
            switch (Name(name ?? ""))
            {
                case Default: return PutBackDefault(sapi, (argument ?? "").Trim(), lang);
                case MethodsOn:
                {
                    var cfg = Preservation.Config;
                    cfg.EnableWaxDipping = cfg.EnableResinLacquer = cfg.EnableFormaldehydeBarrel = cfg.EnableSaltLimeCure = true;
                    string kept = Save(sapi);
                    return (T(lang, "fix-methods-on-done") + Kept(lang, kept) + " " + T(lang, "fix-restart"), false);
                }
                case DropUnmatched:
                {
                    var typos = ModChecks.UnmatchedListEntries(sapi.World).typos;
                    if (typos.Count == 0) return (T(lang, "fix-drop-nothing"), false);
                    var cfg = Preservation.Config;
                    cfg.AlwaysPreservable = (cfg.AlwaysPreservable ?? Array.Empty<string>()).Where(e => !typos.Contains(("AlwaysPreservable", e))).ToArray();
                    cfg.NeverPreservable = (cfg.NeverPreservable ?? Array.Empty<string>()).Where(e => !typos.Contains(("NeverPreservable", e))).ToArray();
                    string kept = Save(sapi);
                    return (T(lang, "fix-drop-done", string.Join(", ", typos.Select(t => t.entry))) + Kept(lang, kept) + " " + T(lang, "fix-restart"), false);
                }
                case Cure: return CurePlayers(sapi, (argument ?? "").Trim(), lang);
                default: return (T(lang, "fix-unknown", name ?? ""), true);
            }
        }
        catch (Exception e)
        {
            sapi.Logger.Warning("[Embalmed Eats] Fix {0} ({1}) failed: {2}", name, argument, e);
            return (T(lang, "fix-failed", e.Message), true);
        }
    }

    static (string, bool) PutBackDefault(ICoreServerAPI sapi, string setting, string lang)
    {
        FieldInfo? field = typeof(EmbalmedEatsConfig).GetFields(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(f => string.Equals(f.Name, setting, StringComparison.OrdinalIgnoreCase));
        if (field == null) return (T(lang, "fix-unknown-setting", setting), true);

        // A copy of the default (arrays and the toxicity stages are objects of their own)
        object? fresh = field.GetValue(new EmbalmedEatsConfig());
        object? copy = fresh == null ? null : JsonConvert.DeserializeObject(JsonConvert.SerializeObject(fresh), field.FieldType);
        string before = JsonConvert.SerializeObject(field.GetValue(Preservation.Config));
        field.SetValue(Preservation.Config, copy);
        string kept = Save(sapi);

        string result = T(lang, "fix-default-done", field.Name, Short(before), Short(JsonConvert.SerializeObject(copy))) + Kept(lang, kept);
        if (NeedRestart.Contains(field.Name)) result += " " + T(lang, "fix-restart");
        return (result, false);
    }

    static (string, bool) CurePlayers(ICoreServerAPI sapi, string who, string lang)
    {
        var tox = ToxicitySystem.Instance;
        if (tox == null) return (T(lang, "fix-cure-notrunning"), true);
        var players = sapi.World.AllOnlinePlayers.OfType<IServerPlayer>()
            .Where(p => p.Entity != null && (who == "all" || who == "" || p.PlayerUID == who)).ToList();
        int cured = 0;
        foreach (var p in players)
        {
            if (tox.GetToxicity(p.Entity) <= 0) continue;
            tox.Cure(p.Entity);
            cured++;
        }
        if (who != "all" && who != "" && players.Count == 0) return (T(lang, "fix-cure-noplayer"), true);
        return (T(lang, "fix-cure-done", cured), false);
    }

    static string Kept(string lang, string kept) => string.IsNullOrEmpty(kept) ? "" : " " + T(lang, "fix-kept", kept);

    static string Short(string text) => text.Length <= 80 ? text : text.Substring(0, 77) + "...";

    /// <summary>
    /// Writes the settings file (copying the old one to the backups first), clears what was worked out from the old
    /// settings, and sends the new ones to everyone playing. Returns where the old file went ("" when there was none).
    /// </summary>
    public static string Save(ICoreServerAPI sapi)
    {
        string kept = Backup(sapi, EmbalmedEatsModSystem.ConfigFile) ?? "";
        sapi.StoreModConfig(Preservation.Config, EmbalmedEatsModSystem.ConfigFile);
        Preservation.ClearCache();
        sapi.ModLoader.GetModSystem<EmbalmedEatsModSystem>()?.SendConfigToAll();
        return kept;
    }

    /// <summary>Copies ModConfig/&lt;file&gt; to ModData/embalmedeats/backups/&lt;date and time&gt;/. Returns the copy's path inside the data folder, or null.</summary>
    public static string? Backup(ICoreAPI api, string file)
    {
        string source = Path.Combine(GamePaths.ModConfig, file);
        if (!File.Exists(source)) return null;
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string folder = api.GetOrCreateDataPath(Path.Combine("ModData", EmbalmedEatsModSystem.ModId, "backups", stamp));
        string target = Path.Combine(folder, file);
        for (int i = 2; File.Exists(target); i++) target = Path.Combine(folder, Path.GetFileNameWithoutExtension(file) + "-" + i + Path.GetExtension(file));
        File.Copy(source, target);
        return Path.Combine("ModData", EmbalmedEatsModSystem.ModId, "backups", stamp, Path.GetFileName(target)).Replace('\\', '/');
    }
}
