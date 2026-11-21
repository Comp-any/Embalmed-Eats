using System;
using System.Collections.Generic;
using System.Linq;
using EmbalmedEats.Config;
using EmbalmedEats.Core;
using EmbalmedEats.Decoration;
using EmbalmedEats.Patches;
using EmbalmedEats.Recipes;
using EmbalmedEats.Toxicity;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace EmbalmedEats.Doctor;

/// <summary>
/// Our page in Vintage Doctor's window: /doctor embalmedeats ("Something is wrong with preserved food"), and
/// /doctor embalmedeats item &lt;code&gt; ("Why can't I preserve this?"). Built on the server.
/// </summary>
public static class DoctorPage
{
    static string T(string lang, string key, params object[] args) => DoctorText.T(lang, key, args);

    /// <param name="argument">"" for the whole page, "item &lt;code&gt;" for one thing.</param>
    /// <param name="heldCode">What the asker holds, as the doctor writes it ("item:game:bread-spelt-perfect"), or null.</param>
    public static PageLines Build(ICoreServerAPI sapi, string lang, string argument, byte[]? clientFacts, string? heldCode)
    {
        var page = new PageLines();
        string arg = (argument ?? "").Trim();
        if (arg.StartsWith("item", StringComparison.OrdinalIgnoreCase))
        {
            string code = arg.Length > 4 ? arg.Substring(4).Trim() : "";
            if (code.Length == 0) code = heldCode ?? "";
            CollectibleObject? coll = Find(sapi.World, code);
            if (coll == null)
            {
                page.Heading(T(lang, "page-title"));
                page.Warn(code.Length == 0 ? T(lang, "item-none-given") : T(lang, "item-unknown", code));
            }
            else FoodCards.ItemPage(sapi.World, lang, coll, page);
            page.Text(PageLines.OpenToken(DoctorBridge.TopicId, "", T(lang, "link-page")));
            return page;
        }

        Whole(sapi, lang, clientFacts, heldCode, page);
        return page;
    }

    /// <summary>An item or block by code: "game:bread-spelt-perfect", "item:game:…" or "block:game:…".</summary>
    public static CollectibleObject? Find(IWorldAccessor world, string code)
    {
        code = (code ?? "").Trim();
        if (code.Length == 0) return null;
        bool? block = null;
        if (code.StartsWith("item:", StringComparison.Ordinal)) { block = false; code = code.Substring(5); }
        else if (code.StartsWith("block:", StringComparison.Ordinal)) { block = true; code = code.Substring(6); }
        var loc = new AssetLocation(code);
        if (block != true && world.GetItem(loc) is Item item && !item.IsMissing) return item;
        if (block != false && world.GetBlock(loc) is Block b && b.Id != 0 && !b.IsMissing) return b;
        return null;
    }

    static void Whole(ICoreServerAPI sapi, string lang, byte[]? clientFacts, string? heldCode, PageLines page)
    {
        var world = sapi.World;
        var cfg = Preservation.Config;
        string version = sapi.ModLoader.GetModSystem<EmbalmedEatsModSystem>()?.Mod?.Info?.Version ?? "?";

        page.Heading(T(lang, "page-title"));
        page.Note(T(lang, "page-intro", version));

        // The same cards as on the check-up, with their buttons
        foreach (Finding f in ModChecks.Server(sapi, lang, clientFacts))
        {
            if (f.Link == DoctorBridge.TopicId && string.IsNullOrEmpty(f.LinkArgument)) f.Link = null; // we're on that page
            page.Card(f);
        }
        if (heldCode != null && Find(world, heldCode) is CollectibleObject held)
        {
            page.Text(PageLines.OpenToken(DoctorBridge.TopicId, "item " + held.Code, T(lang, "link-held", ModChecks.ThingName(held))));
        }

        // What counts as food
        page.Heading(T(lang, "page-food"));
        List<CollectibleObject> foods = world.Collectibles.Where(c => c?.Code != null && Preservation.IsPreservableCollectible(c)).ToList();
        page.Text(T(lang, "foods-ok", foods.Count, foods.Count(c => c.Code.Domain != "game")));
        foreach (var group in foods.GroupBy(c => c.Code.Domain).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(15))
        {
            page.Note(T(lang, "foods-by-mod", ModChecks.ModName(sapi, lang, group.Key), group.Count()), 1);
        }
        foreach (var (setting, entries) in new[] { ("AlwaysPreservable", cfg.AlwaysPreservable), ("NeverPreservable", cfg.NeverPreservable) })
        {
            page.Text(setting + ": " + ModChecks.Join(entries));
            foreach (string entry in entries ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                var pattern = new AssetLocation(entry);
                int n = world.Collectibles.Count(c => c?.Code != null && Vintagestory.API.Util.WildcardUtil.Match(pattern, c.Code));
                page.Note(T(lang, "page-entry-matches", entry, n), 1);
            }
        }
        page.Note(T(lang, "page-food-rules"));

        // The methods
        page.Heading(T(lang, "page-methods"));
        foreach (string how in FoodCards.HowToPreserve(world, lang)) page.Text(how);
        foreach (var (method, on) in new[]
                 {
                     (PreservationMethods.Wax, cfg.EnableWaxDipping), (PreservationMethods.Resin, cfg.EnableResinLacquer),
                     (PreservationMethods.Formaldehyde, cfg.EnableFormaldehydeBarrel), (PreservationMethods.SaltCure, cfg.EnableSaltLimeCure)
                 })
        {
            if (!on) page.Note(T(lang, "method-off", DoctorText.MethodTitle(lang, method)));
        }
        page.Note(T(lang, "page-recipes", GridRecipeGenerator.Generated));

        // Decoration
        page.Heading(T(lang, "page-display"));
        page.Text(T(lang, "page-shelves", OnOff(lang, cfg.AllowOnShelves)));
        bool caseSupport = !PatchUtil.Failures.Any(f => f.StartsWith(DisplayPatches.Label + ":", StringComparison.Ordinal));
        page.Text(T(lang, "page-displaycases", OnOff(lang, cfg.AllowInDisplayCases && caseSupport)));
        page.Text(T(lang, "page-ground", OnOff(lang, cfg.AllowOnGround), world.Items.Count(i => i?.Code != null && GroundStorageSupport.IsAddedFor(i))));
        page.Text(T(lang, "page-scale", DoctorText.Num(cfg.FallbackDisplayScale)));

        // Eating it
        page.Heading(T(lang, "page-eating"));
        foreach (ToxicityStage s in (cfg.ToxicityStages ?? Array.Empty<ToxicityStage>()).Where(s => s != null).OrderBy(s => s.Threshold))
        {
            page.Text(T(lang, "page-stage", DoctorText.Num(s.Threshold), s.Name, DoctorText.Num(s.SatietyLossPercent), DoctorText.Num(s.PoisonDamage),
                DoctorText.Num(s.Nausea), DoctorText.Num(s.SlownessPercent)));
        }
        page.Note(cfg.ToxicityDecayPerGameHour > 0
            ? T(lang, "page-decay", DoctorText.Num(cfg.ToxicityPerItem), DoctorText.Num(1 / cfg.ToxicityDecayPerGameHour), DoctorText.Num(Math.Max(1, cfg.MaxToxicity)))
            : T(lang, "tox-never"));

        var tox = ToxicitySystem.Instance;
        var poisoned = tox == null ? new List<(IServerPlayer player, float value)>() : world.AllOnlinePlayers.OfType<IServerPlayer>()
            .Where(p => p.Entity != null).Select(p => (player: p, value: tox.GetToxicity(p.Entity))).Where(p => p.value > 0.01f).ToList();
        if (poisoned.Count == 0) page.Text(T(lang, "page-nobody-poisoned"));
        foreach (var (player, value) in poisoned)
        {
            string stage = ToxicitySystem.StageFor(value, cfg.ToxicityStages)?.Name ?? T(lang, "tox-mild");
            page.Text(T(lang, "page-poisoned", player.PlayerName, DoctorText.Num(value), stage) + " " +
                PageLines.FixToken(DoctorFixes.Id(DoctorFixes.Cure), player.PlayerUID, T(lang, "fix-cure-label", player.PlayerName)));
        }
        if (poisoned.Count > 1) page.Text(PageLines.FixToken(DoctorFixes.Id(DoctorFixes.Cure), "all", T(lang, "fix-cure-all-label")));

        // Its changes to the game
        page.Heading(T(lang, "page-changes"));
        foreach (string feature in ModChecks.Features)
        {
            int made = PatchUtil.CountPatched(feature);
            int failed = PatchUtil.Failures.Count(f => ModChecks.FeatureOf(f) == feature);
            string line = T(lang, "page-feature", T(lang, "part-" + feature), made);
            if (failed > 0) page.Warn(line + " " + T(lang, "page-feature-failed", failed));
            else page.Text(line);
        }

        page.Text(PageLines.OpenToken("mod", EmbalmedEatsModSystem.ModId, T(lang, "link-mod")) + " " +
                  PageLines.OpenToken("log", EmbalmedEatsModSystem.ModId, T(lang, "link-log")));
    }

    static string OnOff(string lang, bool on) => T(lang, on ? "on" : "off");
}
