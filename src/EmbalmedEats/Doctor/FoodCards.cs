using System;
using System.Collections.Generic;
using System.Linq;
using EmbalmedEats.Core;
using EmbalmedEats.Decoration;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace EmbalmedEats.Doctor;

/// <summary>
/// Cards for Vintage Doctor's pages about one item or block ("vintagedoctor:about"): can Embalmed Eats preserve it,
/// why or why not, and how. Also "Why can't I preserve this?" in detail, for our own page.
/// </summary>
public static class FoodCards
{
    static string T(string lang, string key, params object[] args) => DoctorText.T(lang, key, args);

    /// <summary>The words for the rule that decided (texts "doctor-reason-…").</summary>
    public static string Reason(string lang, FoodVerdict verdict) =>
        T(lang, "reason-" + verdict.Reason.ToString().ToLowerInvariant(), verdict.Pattern ?? "");

    /// <summary>
    /// The cards about <paramref name="coll"/>. Nothing for things nobody would call food (a pickaxe), unless a
    /// setting or its mod ruled it out on purpose. <paramref name="held"/>: what the asker holds, if it's this thing.
    /// </summary>
    public static List<Finding> About(IWorldAccessor world, string lang, CollectibleObject coll, ItemStack? held)
    {
        var list = new List<Finding>();
        FoodVerdict verdict = Preservation.Explain(coll);

        if (verdict.Preservable)
        {
            var f = new Finding("about", Level.Info, T(lang, "about-yes", Reason(lang, verdict)));
            foreach (string how in HowToPreserve(world, lang)) f.Detail(how);
            if (coll is IBlockMealContainer) f.Detail(T(lang, "about-needs-meal"));
            if (GroundStorageSupport.IsAddedFor(coll)) f.Detail(T(lang, "about-ground-when-preserved"));
            list.Add(f);
        }
        else if (Preservation.LooksLikeFood(coll) || verdict.Reason == FoodReason.NeverList || verdict.Reason == FoodReason.ModSaysNo)
        {
            var f = new Finding("about", Level.Info, T(lang, "about-no", Reason(lang, verdict)));
            if (verdict.Reason == FoodReason.NeverList) f.WithAdvice(T(lang, "about-no-never-advice", verdict.Pattern ?? ""));
            else if (verdict.Reason == FoodReason.Liquid || verdict.Reason == FoodReason.ModSaysNo) f.WithAdvice(T(lang, "about-no-always-advice"));
            list.Add(f);
        }

        if (held?.Collectible == coll)
        {
            string? method = Preservation.GetMethod(held);
            if (method != null)
            {
                var f = new Finding("about-held", Level.Info, T(lang, "about-held-preserved", DoctorText.MethodName(lang, method)));
                var cfg = Preservation.Config;
                var places = new List<string>();
                if (cfg.AllowOnShelves) places.Add(T(lang, "place-shelves"));
                if (cfg.AllowInDisplayCases) places.Add(T(lang, "place-displaycases"));
                if (cfg.AllowOnGround && GroundStorageSupport.CanGoOnGround(held)) places.Add(T(lang, "place-ground"));
                f.Detail(places.Count > 0 ? T(lang, "about-held-places", string.Join(", ", places)) : T(lang, "about-held-noplaces"));
                list.Add(f);
            }
            else if (verdict.Preservable && coll is IBlockMealContainer && !Preservation.CanPreserve(held))
            {
                list.Add(new Finding("about-held", Level.Info, T(lang, "about-held-empty")));
            }
        }
        return list;
    }

    /// <summary>One line per switched-on method: what it takes ("Wax dipping: in the crafting grid with 1 Beeswax").</summary>
    public static List<string> HowToPreserve(IWorldAccessor world, string lang)
    {
        var cfg = Preservation.Config;
        var lines = new List<string>();
        int per = Math.Max(1, cfg.PreservativePerFood);

        void Grid(string method, bool on, string[] items)
        {
            if (!on) return;
            CollectibleObject? first = ModChecks.PreservativesFor(world, items).FirstOrDefault();
            string what = first != null ? ModChecks.ThingName(first) : ModChecks.Join(items);
            lines.Add(T(lang, "how-grid", DoctorText.MethodTitle(lang, method), per, what));
        }

        Grid(PreservationMethods.Wax, cfg.EnableWaxDipping, cfg.WaxItems);
        Grid(PreservationMethods.Resin, cfg.EnableResinLacquer, cfg.LacquerItems);
        if (cfg.EnableFormaldehydeBarrel)
        {
            lines.Add(T(lang, "how-formaldehyde", DoctorText.MethodTitle(lang, PreservationMethods.Formaldehyde),
                DoctorText.Num(Math.Max(0, cfg.FormaldehydeLitresPerFood)), DoctorText.Num(Math.Max(0.1, cfg.FormaldehydeSealHours))));
        }
        if (cfg.EnableSaltLimeCure)
        {
            lines.Add(T(lang, "how-saltcure", DoctorText.MethodTitle(lang, PreservationMethods.SaltCure),
                Math.Max(1, cfg.CuringSaltPerFood), DoctorText.Num(Math.Max(0.1, cfg.SaltCureSealHours))));
        }
        if (lines.Count == 0) lines.Add(T(lang, "how-none"));
        return lines;
    }

    /// <summary>
    /// Every rule, in the order they're tried, with what each says about this thing; the first that applies decides.
    /// For our page's "Why can't I preserve this?" (/doctor embalmedeats item &lt;code&gt;).
    /// </summary>
    public static void ItemPage(IWorldAccessor world, string lang, CollectibleObject coll, PageLines page)
    {
        var cfg = Preservation.Config;
        var attrs = coll.Attributes;
        FoodVerdict verdict = Preservation.Explain(coll);
        string Yes(bool b) => b ? T(lang, "yes") : T(lang, "no");

        page.Heading(T(lang, "item-title", PageLines.ThingToken(coll)));
        page.Card(new Finding("item", verdict.Preservable ? Level.Ok : Level.Info,
            T(lang, verdict.Preservable ? "about-yes" : "about-no", Reason(lang, verdict))));

        page.Text(T(lang, "item-rules"));
        page.Note(T(lang, "rule-never", Preservation.FirstMatch(cfg.NeverPreservable, coll.Code) ?? T(lang, "no-match")), 1);
        page.Note(T(lang, "rule-always", Preservation.FirstMatch(cfg.AlwaysPreservable, coll.Code) ?? T(lang, "no-match")), 1);
        page.Note(T(lang, "rule-attribute", attrs?["embalmedeatsPreservable"].Exists == true ? attrs["embalmedeatsPreservable"].AsBool().ToString().ToLowerInvariant() : T(lang, "not-set")), 1);
        page.Note(T(lang, "rule-own", Yes(coll.Code.Domain == "embalmedeats")), 1);
        page.Note(T(lang, "rule-liquid", Yes(coll.MatterState == EnumMatterState.Liquid || coll.MatterState == EnumMatterState.Gas || attrs?["waterTightContainerProps"].Exists == true)), 1);
        page.Note(T(lang, "rule-meal", Yes(coll is IBlockMealContainer)), 1);
        page.Note(T(lang, "rule-nutrition", coll.NutritionProps != null ? T(lang, "yes-satiety", DoctorText.Num(coll.NutritionProps.Satiety)) : T(lang, "no")), 1);
        page.Note(T(lang, "rule-ingredient", Yes(attrs?["nutritionPropsWhenInMeal"].Exists == true)), 1);
        page.Note(T(lang, "rule-rots", Yes(coll.TransitionableProps?.Any(p => p?.Type == EnumTransitionType.Perish) == true)), 1);

        if (verdict.Preservable)
        {
            page.Text(T(lang, "item-how"));
            foreach (string how in HowToPreserve(world, lang)) page.Note(how, 1);
            if (coll is IBlockMealContainer) page.Note(T(lang, "about-needs-meal"), 1);
        }
        else if (verdict.Reason == FoodReason.NeverList)
        {
            page.Text("→ " + T(lang, "about-no-never-advice", verdict.Pattern ?? ""));
        }
        else if (verdict.Reason != FoodReason.OwnItem && verdict.Reason != FoodReason.Missing)
        {
            page.Text("→ " + T(lang, "about-no-always-advice"));
        }
    }
}
