using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EmbalmedEats.Config;
using EmbalmedEats.Core;
using EmbalmedEats.Decoration;
using EmbalmedEats.Patches;
using EmbalmedEats.Recipes;
using EmbalmedEats.Toxicity;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace EmbalmedEats.Doctor;

public static class DoctorText
{
    /// <summary>A text of ours ("embalmedeats:doctor-…") in the asking player's language.</summary>
    public static string T(string? lang, string key, params object[] args) => Lang.GetL(lang ?? "en", "embalmedeats:doctor-" + key, args);

    public static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    public static string MethodName(string? lang, string method) => Lang.GetL(lang ?? "en", "embalmedeats:method-" + method);

    /// <summary>The method's name for the start of a line ("Wax dipping").</summary>
    public static string MethodTitle(string? lang, string method)
    {
        string name = MethodName(lang, method);
        return name.Length == 0 ? name : char.ToUpper(name[0], CultureInfo.InvariantCulture) + name.Substring(1);
    }
}

/// <summary>
/// The sanity checks: whether Embalmed Eats set itself up properly (its changes to the game, the food it found, the
/// four methods, the settings), and whether a player's game agrees with the server. Used by the Vintage Doctor
/// bridge and by /embalmedeats check, so they work with or without the doctor.
/// </summary>
public static class ModChecks
{
    static string T(string lang, string key, params object[] args) => DoctorText.T(lang, key, args);

    /// <summary>The patch labels, each a part of what the mod does (texts "doctor-feature-&lt;label&gt;").</summary>
    public static readonly string[] Features =
    {
        TransitionPatches.Label, NutritionPatches.Label, EatingPatches.Label, CraftingPatches.Label, NamePatches.Label,
        BarrelPatches.Label, DisplayPatches.Label, GroundStoragePatches.Label, HandbookPatches.Label
    };

    /// <summary>Without these the mod doesn't do what it's for (food would rot, feed you, or be safe to eat).</summary>
    static readonly string[] CoreFeatures = { TransitionPatches.Label, NutritionPatches.Label, EatingPatches.Label };

    /// <summary>Everything the server can check about itself. <paramref name="clientFacts"/>: what the asking player's game sent (null for the console).</summary>
    public static List<Finding> Server(ICoreServerAPI sapi, string lang, byte[]? clientFacts)
    {
        var list = new List<Finding>();
        Guard(list, lang, "changes", () => GameChanges(lang, list));
        Guard(list, lang, "foods", () => Foods(sapi, lang, list));
        Guard(list, lang, "methods", () => Methods(sapi, lang, list));
        Guard(list, lang, "settings", () => Settings(sapi, lang, list));
        if (clientFacts != null) Guard(list, lang, "sync", () => ClientFacts.Compare(sapi, lang, clientFacts, list));
        return list;
    }

    /// <summary>What a player's own game can check: their poisoning, and in multiplayer whether the server's settings arrived.</summary>
    public static List<Finding> Client(ICoreClientAPI capi, string lang)
    {
        var list = new List<Finding>();
        Guard(list, lang, "toxicity", () => Toxicity(capi, lang, list));
        if (!capi.IsSinglePlayer)
        {
            // In single player the server's half says these, about the same game
            Guard(list, lang, "settings", () =>
            {
                if (EmbalmedEatsModSystem.ConfigReceived) list.Add(new Finding("settings", Level.Ok, T(lang, "settings-received")));
                else list.Add(new Finding("settings", Level.Warning, T(lang, "settings-missing")).WithAdvice(T(lang, "settings-missing-advice")));
            });
            Guard(list, lang, "changes", () => GameChanges(lang, list));
        }
        return list;
    }

    /// <summary>One check that throws doesn't take the others with it: it becomes a card saying so.</summary>
    static void Guard(List<Finding> list, string lang, string name, Action check)
    {
        try { check(); }
        catch (Exception e)
        {
            list.Add(new Finding("check-failed-" + name, Level.Warning, T(lang, "check-failed", name))
                .Detail(e.GetType().Name + ": " + e.Message));
        }
    }

    // ------------------------------------------------------------------ its changes to the game (Harmony)

    public static void GameChanges(string lang, List<Finding> list)
    {
        List<string> failures = PatchUtil.Failures.ToList();
        int made = PatchUtil.Patched.Count;
        if (failures.Count == 0)
        {
            list.Add(new Finding("changes", made > 0 ? Level.Ok : Level.Problem, made > 0 ? T(lang, "changes-ok", made) : T(lang, "changes-none"))
                .Detail(T(lang, "changes-ok-detail")));
            return;
        }

        bool core = failures.Any(f => CoreFeatures.Contains(FeatureOf(f)));
        var finding = new Finding("changes", core ? Level.Problem : Level.Warning, T(lang, "changes-failed", failures.Count, made));
        foreach (string failure in failures.Take(30))
        {
            finding.Detail(T(lang, "feature-" + FeatureOf(failure)) + ": " + failure);
        }
        finding.WithAdvice(T(lang, "changes-advice")).WithLink("log", EmbalmedEatsModSystem.ModId, T(lang, "link-log"));
        list.Add(finding);
    }

    /// <summary>Which part of the mod a failure is about ("display: …" → display); "other" when it doesn't say.</summary>
    public static string FeatureOf(string failure)
    {
        int colon = failure.IndexOf(':');
        string label = colon > 0 ? failure.Substring(0, colon).Trim() : "";
        return Features.Contains(label) ? label : "other";
    }

    // ------------------------------------------------------------------ the food it found

    static void Foods(ICoreServerAPI sapi, string lang, List<Finding> list)
    {
        var world = sapi.World;
        List<CollectibleObject> foods = world.Collectibles.Where(c => c?.Code != null && Preservation.IsPreservableCollectible(c)).ToList();
        if (foods.Count == 0)
        {
            var none = new Finding("foods", Level.Problem, T(lang, "foods-none"))
                .Detail("NeverPreservable: " + Join(Preservation.Config.NeverPreservable))
                .WithAdvice(T(lang, "foods-none-advice"));
            if (!SameList(Preservation.Config.NeverPreservable, new EmbalmedEatsConfig().NeverPreservable))
            {
                none.WithFix(DoctorFixes.Default, "NeverPreservable", T(lang, "fix-default-label", "NeverPreservable"));
            }
            list.Add(none);
            return;
        }

        int fromMods = foods.Count(c => c.Code.Domain != "game");
        var finding = new Finding("foods", Level.Ok, T(lang, "foods-ok", foods.Count, fromMods));
        foreach (var group in foods.GroupBy(c => c.Code.Domain).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(10))
        {
            finding.Detail(T(lang, "foods-by-mod", ModName(sapi, lang, group.Key), group.Count()));
        }
        if (Preservation.Config.AllowOnGround)
        {
            finding.Detail(T(lang, "foods-ground", world.Items.Count(i => i?.Code != null && GroundStorageSupport.IsAddedFor(i))));
        }
        finding.WithLink(DoctorBridge.TopicId, "", T(lang, "link-page"));
        list.Add(finding);
    }

    public static string ModName(ICoreAPI api, string lang, string domain)
    {
        if (domain == "game") return T(lang, "the-game");
        try { return api.ModLoader.GetMod(domain)?.Info?.Name ?? domain; }
        catch { return domain; }
    }

    // ------------------------------------------------------------------ the four methods

    static void Methods(ICoreServerAPI sapi, string lang, List<Finding> list)
    {
        var cfg = Preservation.Config;
        var world = sapi.World;
        var working = new List<string>();
        int switchedOn = 0;

        void Grid(string method, bool on, string[] items, string setting)
        {
            string name = DoctorText.MethodName(lang, method), title = DoctorText.MethodTitle(lang, method);
            if (!on) { working.Add(T(lang, "method-off", title)); return; }
            switchedOn++;
            List<CollectibleObject> found = PreservativesFor(world, items);
            int recipes = GridRecipeGenerator.Recipes.Count(r => r.Attributes?["embalmedeatsMethod"].AsString() == method);
            int per = Math.Max(1, cfg.PreservativePerFood);
            if (found.Count == 0)
            {
                list.Add(new Finding("method-" + method, Level.Warning, T(lang, "method-no-items", name, setting))
                    .Detail(setting + ": " + Join(items))
                    .WithAdvice(T(lang, "method-no-items-advice", setting))
                    .WithFix(DoctorFixes.Default, setting, T(lang, "fix-default-label", setting)));
                return;
            }
            if (recipes == 0)
            {
                list.Add(new Finding("method-" + method, Level.Warning, T(lang, "method-no-recipes", name))
                    .WithAdvice(T(lang, "method-no-recipes-advice"))
                    .WithLink("log", EmbalmedEatsModSystem.ModId, T(lang, "link-log")));
                return;
            }
            int maxStack = found.Max(c => c.MaxStackSize);
            if (per > maxStack)
            {
                list.Add(new Finding("method-" + method, Level.Warning, T(lang, "method-too-many", name, per, maxStack))
                    .WithFix(DoctorFixes.Default, "PreservativePerFood", T(lang, "fix-default-label", "PreservativePerFood")));
                return;
            }
            working.Add(T(lang, "method-grid-ok", title, ThingName(found[0]), per, recipes));
            var skipped = GridRecipeGenerator.Skipped.Where(s => s.method == method).ToList();
            if (skipped.Count > 0)
            {
                string examples = string.Join("; ", skipped.Take(4).Select(s =>
                    ThingName(s.food) + " + " + ThingName(s.preservative) + " → " + (s.other.Output?.ResolvedItemStack?.Collectible is CollectibleObject made ? ThingName(made) : s.other.Output?.Code?.ToString() ?? "?")));
                if (skipped.Count > 4) examples += " " + T(lang, "and-more", skipped.Count - 4);
                working.Add(T(lang, "method-grid-skipped", title, skipped.Count, examples));
            }
        }

        Grid(PreservationMethods.Wax, cfg.EnableWaxDipping, cfg.WaxItems, "WaxItems");
        Grid(PreservationMethods.Resin, cfg.EnableResinLacquer, cfg.LacquerItems, "LacquerItems");

        // Formaldehyde: the solution must be makeable, and a food's share must fit in a barrel
        string formName = DoctorText.MethodName(lang, PreservationMethods.Formaldehyde), formTitle = DoctorText.MethodTitle(lang, PreservationMethods.Formaldehyde);
        if (!cfg.EnableFormaldehydeBarrel) working.Add(T(lang, "method-off", formTitle));
        else
        {
            switchedOn++;
            Item? fluid = world.GetItem(new AssetLocation(EmbalmingBarrelRecipe.FormaldehydeCode));
            bool makeable = fluid != null && sapi.GetBarrelRecipes().Any(r => r.Output?.ResolvedItemStack?.Collectible == fluid);
            int capacity = BarrelCapacity(world);
            if (fluid == null)
            {
                list.Add(new Finding("method-formaldehyde", Level.Problem, T(lang, "method-item-missing", formName, EmbalmingBarrelRecipe.FormaldehydeCode)));
            }
            else if (!makeable)
            {
                var f = new Finding("method-formaldehyde", Level.Warning, T(lang, "method-formaldehyde-norecipe"));
                foreach (string code in new[] { "game:alcoholportion", "game:powder-borax" })
                {
                    if (world.GetItem(new AssetLocation(code)) == null) f.Detail(T(lang, "method-ingredient-missing", code));
                }
                list.Add(f.WithAdvice(T(lang, "method-formaldehyde-norecipe-advice")));
            }
            else if (cfg.FormaldehydeLitresPerFood > capacity)
            {
                list.Add(new Finding("method-formaldehyde", Level.Warning, T(lang, "method-formaldehyde-toomuch", DoctorText.Num(cfg.FormaldehydeLitresPerFood), capacity))
                    .WithFix(DoctorFixes.Default, "FormaldehydeLitresPerFood", T(lang, "fix-default-label", "FormaldehydeLitresPerFood")));
            }
            else
            {
                working.Add(T(lang, "method-formaldehyde-ok", formTitle, DoctorText.Num(Math.Max(0, cfg.FormaldehydeLitresPerFood)), DoctorText.Num(Math.Max(0.1, cfg.FormaldehydeSealHours))));
            }
        }

        // Salt & lime: curing salt must be craftable, and a food's share must fit in a barrel slot
        string saltName = DoctorText.MethodName(lang, PreservationMethods.SaltCure), saltTitle = DoctorText.MethodTitle(lang, PreservationMethods.SaltCure);
        if (!cfg.EnableSaltLimeCure) working.Add(T(lang, "method-off", saltTitle));
        else
        {
            switchedOn++;
            Item? salt = world.GetItem(new AssetLocation(EmbalmingBarrelRecipe.CuringSaltCode));
            bool craftable = salt != null && world.GridRecipes.Any(r => r.Output?.ResolvedItemStack?.Collectible == salt);
            int per = Math.Max(1, cfg.CuringSaltPerFood);
            if (salt == null)
            {
                list.Add(new Finding("method-saltcure", Level.Problem, T(lang, "method-item-missing", saltName, EmbalmingBarrelRecipe.CuringSaltCode)));
            }
            else if (!craftable)
            {
                var f = new Finding("method-saltcure", Level.Warning, T(lang, "method-saltcure-norecipe"));
                foreach (string code in new[] { "game:salt", "game:lime" })
                {
                    if (world.GetItem(new AssetLocation(code)) == null) f.Detail(T(lang, "method-ingredient-missing", code));
                }
                list.Add(f);
            }
            else if (per > salt.MaxStackSize)
            {
                list.Add(new Finding("method-saltcure", Level.Warning, T(lang, "method-saltcure-toomuch", per, salt.MaxStackSize))
                    .WithFix(DoctorFixes.Default, "CuringSaltPerFood", T(lang, "fix-default-label", "CuringSaltPerFood")));
            }
            else
            {
                working.Add(T(lang, "method-saltcure-ok", saltTitle, per, DoctorText.Num(Math.Max(0.1, cfg.SaltCureSealHours))));
            }
        }

        if (switchedOn == 0)
        {
            list.Add(new Finding("methods", Level.Warning, T(lang, "methods-all-off"))
                .WithAdvice(T(lang, "methods-all-off-advice"))
                .WithFix(DoctorFixes.MethodsOn, "", T(lang, "fix-methods-on-label")));
            return;
        }

        int broken = list.Count(f => f.Key.StartsWith("method-", StringComparison.Ordinal));
        var ok = new Finding("methods", Level.Ok, broken == 0 ? T(lang, "methods-ok", switchedOn) : T(lang, "methods-some", switchedOn - broken, switchedOn));
        foreach (string line in working) ok.Detail(line);
        list.Add(ok);
    }

    /// <summary>The items and blocks that count as wax or resin (WaxItems, LacquerItems).</summary>
    public static List<CollectibleObject> PreservativesFor(IWorldAccessor world, string[]? patterns) =>
        world.Collectibles.Where(c => c?.Code != null && !c.IsMissing && Preservation.MatchesAny(patterns, c.Code)).ToList();

    public static string ThingName(CollectibleObject coll)
    {
        try { return new ItemStack(coll).GetName(); }
        catch { return coll.Code.ToString(); }
    }

    /// <summary>How many litres a barrel holds (50 in the game; read from the barrel in case a mod changed it).</summary>
    public static int BarrelCapacity(IWorldAccessor world)
    {
        Block? barrel = world.GetBlock(new AssetLocation("game:barrel"));
        return barrel?.Attributes?["capacityLitres"].AsInt(50) ?? 50;
    }

    // ------------------------------------------------------------------ the settings

    /// <summary>Settings that don't do what their owner most likely wanted, and the settings file itself.</summary>
    public static void Settings(ICoreServerAPI sapi, string lang, List<Finding> list) => Settings(sapi.World, sapi, lang, list);

    public static void Settings(IWorldAccessor world, ICoreAPI api, string lang, List<Finding> list)
    {
        var cfg = Preservation.Config;

        if (EmbalmedEatsModSystem.ConfigLoadError != null)
        {
            var f = new Finding("settings-file", Level.Warning, T(lang, "cfg-unreadable", EmbalmedEatsModSystem.ConfigFile))
                .Detail(EmbalmedEatsModSystem.ConfigLoadError);
            if (EmbalmedEatsModSystem.ConfigBackup != null) f.Detail(T(lang, "cfg-unreadable-kept", EmbalmedEatsModSystem.ConfigBackup));
            list.Add(f.WithAdvice(T(lang, "cfg-unreadable-advice")));
        }

        void Issue(string setting, Level level, string title, string? advice = null, bool fix = true)
        {
            var f = new Finding("cfg-" + setting, level, title).WithAdvice(advice);
            if (fix) f.WithFix(DoctorFixes.Default, setting, T(lang, "fix-default-label", setting));
            list.Add(f);
        }

        // Eating preserved food
        if (cfg.ToxicityPerItem <= 0) Issue("ToxicityPerItem", Level.Warning, T(lang, "cfg-toxperitem", DoctorText.Num(cfg.ToxicityPerItem)));
        if (cfg.ToxicityDecayPerGameHour <= 0) Issue("ToxicityDecayPerGameHour", Level.Warning, T(lang, "cfg-decay", DoctorText.Num(cfg.ToxicityDecayPerGameHour)));

        ToxicityStage[] stages = (cfg.ToxicityStages ?? Array.Empty<ToxicityStage>()).Where(s => s != null).ToArray();
        float max = Math.Max(1, cfg.MaxToxicity);
        List<string> unreachable = stages.Where(s => s.Threshold > max).Select(s => s.Name).ToList();
        if (unreachable.Count > 0) Issue("MaxToxicity", Level.Warning, T(lang, "cfg-maxtox", string.Join(", ", unreachable), DoctorText.Num(max)));

        var twice = stages.GroupBy(s => s.Threshold).Where(g => g.Count() > 1).Select(g => DoctorText.Num(g.Key)).ToList();
        if (twice.Count > 0) Issue("ToxicityStages", Level.Info, T(lang, "cfg-stages-twice", string.Join(", ", twice)), T(lang, "cfg-stages-twice-advice"));

        List<string> wordless = stages.Where(s => !Lang.HasTranslation("embalmedeats:toxicity-" + s.Name, true, false)).Select(s => s.Name).ToList();
        if (wordless.Count > 0)
        {
            list.Add(new Finding("cfg-stage-names", Level.Info, T(lang, "cfg-stage-names", string.Join(", ", wordless)))
                .WithAdvice(T(lang, "cfg-stage-names-advice")));
        }

        // Amounts the mod quietly corrects (said so the owner knows the number in the file isn't used)
        if (cfg.FormaldehydeLitresPerFood < 0) Issue("FormaldehydeLitresPerFood", Level.Info, T(lang, "cfg-below", "FormaldehydeLitresPerFood", DoctorText.Num(cfg.FormaldehydeLitresPerFood), "0"));
        if (cfg.FormaldehydeSealHours < 0.1) Issue("FormaldehydeSealHours", Level.Info, T(lang, "cfg-below", "FormaldehydeSealHours", DoctorText.Num(cfg.FormaldehydeSealHours), "0.1"));
        if (cfg.SaltCureSealHours < 0.1) Issue("SaltCureSealHours", Level.Info, T(lang, "cfg-below", "SaltCureSealHours", DoctorText.Num(cfg.SaltCureSealHours), "0.1"));
        if (cfg.CuringSaltPerFood < 1) Issue("CuringSaltPerFood", Level.Info, T(lang, "cfg-below", "CuringSaltPerFood", cfg.CuringSaltPerFood, "1"));
        if (cfg.PreservativePerFood < 1) Issue("PreservativePerFood", Level.Info, T(lang, "cfg-below", "PreservativePerFood", cfg.PreservativePerFood, "1"));

        // Decoration
        if (cfg.FallbackDisplayScale <= 0) Issue("FallbackDisplayScale", Level.Info, T(lang, "cfg-scale-off", DoctorText.Num(cfg.FallbackDisplayScale)));
        else if (cfg.FallbackDisplayScale > 3) Issue("FallbackDisplayScale", Level.Warning, T(lang, "cfg-scale-big", DoctorText.Num(cfg.FallbackDisplayScale)));

        var off = new List<string>();
        if (!cfg.AllowOnShelves) off.Add("AllowOnShelves");
        if (!cfg.AllowInDisplayCases) off.Add("AllowInDisplayCases");
        if (!cfg.AllowOnGround) off.Add("AllowOnGround");
        if (off.Count > 0)
        {
            list.Add(new Finding("cfg-display", Level.Info, T(lang, "cfg-display-off", string.Join(", ", off)))
                .WithAdvice(T(lang, "cfg-display-off-advice")));
        }

        if (!cfg.BlockPreservedFoodInRecipes) Issue("BlockPreservedFoodInRecipes", Level.Info, T(lang, "cfg-recipes-allowed"), T(lang, "cfg-recipes-allowed-advice"));

        // The food lists: entries that match nothing, for a mod that is installed (a typo); others are for mods not installed
        var unmatched = UnmatchedListEntries(world);
        if (unmatched.typos.Count > 0)
        {
            var f = new Finding("cfg-lists", Level.Info, T(lang, "cfg-lists-unmatched", unmatched.typos.Count));
            foreach (var (setting, entry) in unmatched.typos) f.Detail(setting + ": " + entry);
            if (unmatched.notInstalled > 0) f.Detail(T(lang, "cfg-lists-notinstalled", unmatched.notInstalled));
            list.Add(f.WithAdvice(T(lang, "cfg-lists-unmatched-advice"))
                .WithFix(DoctorFixes.DropUnmatched, "", T(lang, "fix-drop-unmatched-label")));
        }
    }

    /// <summary>
    /// AlwaysPreservable and NeverPreservable entries that match no item or block. "typos" are for the game or a
    /// mod whose things are loaded (so most likely mistyped); entries for mods that aren't installed are only counted.
    /// </summary>
    public static (List<(string setting, string entry)> typos, int notInstalled) UnmatchedListEntries(IWorldAccessor world)
    {
        var cfg = Preservation.Config;
        var typos = new List<(string, string)>();
        int notInstalled = 0;
        var domains = new HashSet<string>(world.Collectibles.Where(c => c?.Code != null).Select(c => c.Code.Domain)) { "game" };

        foreach (var (setting, entries) in new[] { ("AlwaysPreservable", cfg.AlwaysPreservable), ("NeverPreservable", cfg.NeverPreservable) })
        {
            foreach (string entry in entries ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                var pattern = new AssetLocation(entry);
                if (world.Collectibles.Any(c => c?.Code != null && WildcardUtil.Match(pattern, c.Code))) continue;
                if (pattern.Domain.Contains('*') || domains.Contains(pattern.Domain)) typos.Add((setting, entry));
                else notInstalled++;
            }
        }
        return (typos, notInstalled);
    }

    // ------------------------------------------------------------------ the player's own poisoning (their game)

    static void Toxicity(ICoreClientAPI capi, string lang, List<Finding> list)
    {
        var entity = capi.World?.Player?.Entity;
        ITreeAttribute? tree = entity?.WatchedAttributes?.GetTreeAttribute(ToxicitySystem.AttrTree);
        if (tree == null) return;

        var cfg = Preservation.Config;
        double now = capi.World!.Calendar.TotalHours;
        float tox = ToxicitySystem.Decay(tree.GetFloat("toxicity"), tree.GetDouble("lastUpdateHours", now), now, cfg.ToxicityDecayPerGameHour);
        if (tox < 0.01f) return;

        ToxicityStage? stage = ToxicitySystem.StageFor(tox, cfg.ToxicityStages);
        var f = new Finding("toxicity", stage == null ? Level.Info : Level.Warning,
            T(lang, "tox-title", stage?.Name ?? T(lang, "tox-mild"), DoctorText.Num(tox)));
        f.Detail(cfg.ToxicityDecayPerGameHour > 0
            ? T(lang, "tox-wears-off", Math.Ceiling(tox / cfg.ToxicityDecayPerGameHour))
            : T(lang, "tox-never"));
        list.Add(f.WithAdvice(T(lang, "tox-advice")));
    }

    public static string Join(string[]? entries) => entries == null || entries.Length == 0 ? "-" : string.Join(", ", entries);

    public static bool SameList(string[]? a, string[]? b) => (a ?? Array.Empty<string>()).SequenceEqual(b ?? Array.Empty<string>());
}
