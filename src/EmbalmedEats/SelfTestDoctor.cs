using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using EmbalmedEats.Config;
using EmbalmedEats.Core;
using EmbalmedEats.Doctor;
using EmbalmedEats.Toxicity;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace EmbalmedEats;

/// <summary>
/// The self-test's checks of what Embalmed Eats hands Vintage Doctor. They push the doctor's own events
/// ("vintagedoctor:collect", ":about", ":topics", ":page", ":fix") the way the doctor does, so they run with or
/// without the doctor installed; CI also runs a server with the real doctor and reads its pages.
/// </summary>
public static class SelfTestDoctor
{
    static readonly Regex Token = new(@"\{\{(fix|open)\|([^|{}]*)\|([^|{}]*)\|([^|{}]*)\}\}");

    public static void Run(ICoreServerAPI sapi, Action<string, Func<string?>> check)
    {
        var world = sapi.World;
        Item? meat = world.GetItem(new AssetLocation("game:redmeat-raw"));

        check("Vintage Doctor: check-up cards", () =>
        {
            ITreeAttribute results = Collect(sapi, null, null);
            foreach (string key in new[] { "changes", "foods", "methods" })
            {
                if (results["embalmedeats:" + key] is not ITreeAttribute r) return "no card " + key;
                if (r.GetString("level") != "ok") return $"{key} is {r.GetString("level")}: {r.GetString("title")} / {r.GetString("details")}";
            }
            string? bad = BadCards(results);
            if (bad != null) return bad;
            if (Collect(sapi, "someothermod", null).Count != 0) return "answered for another mod's page";
            if (Collect(sapi, "embalmedeats", null).Count == 0) return "nothing on its own mod's page";
            return null;
        });

        check("Vintage Doctor: settings that make no sense are found", () => WithConfig(sapi, cfg =>
        {
            cfg.ToxicityDecayPerGameHour = 0;
            cfg.MaxToxicity = 2;
            cfg.WaxItems = new[] { "game:nosuchwax" };
            cfg.AlwaysPreservable = new[] { "game:nosuchfood-*", "nosuchmod:thing" };
            cfg.FallbackDisplayScale = 10;
            cfg.ToxicityStages = new[]
            {
                new ToxicityStage { Threshold = 1, Name = "queasy", SatietyLossPercent = 30 },
                new ToxicityStage { Threshold = 1, Name = "nosuchstage", SatietyLossPercent = 50 }
            };
        }, () =>
        {
            var found = ModChecks.Server(sapi, "en", null);
            foreach (string key in new[] { "cfg-ToxicityDecayPerGameHour", "method-wax", "cfg-lists", "cfg-FallbackDisplayScale", "cfg-ToxicityStages", "cfg-stage-names" })
            {
                if (!found.Any(f => f.Key == key)) return "not found: " + key + " (found: " + string.Join(", ", found.Select(f => f.Key)) + ")";
            }
            var lists = found.First(f => f.Key == "cfg-lists");
            if (!lists.Details.Any(d => d.Contains("game:nosuchfood-*")) || lists.Details.Any(d => d.Contains("nosuchmod:thing"))) return "food lists: a typo and a mod that isn't installed weren't told apart";
            if (found.First(f => f.Key == "method-wax").Fix != DoctorFixes.Id(DoctorFixes.Default)) return "no fix offered for WaxItems";
            // MaxToxicity 2 with stages at 1 only: nothing unreachable
            if (found.Any(f => f.Key == "cfg-MaxToxicity")) return "MaxToxicity flagged although every stage can be reached";
            return Raw(found);
        }));

        check("Vintage Doctor: fixes", () => WithConfig(sapi, _ => { }, () =>
        {
            var cfg = Preservation.Config;
            cfg.ToxicityDecayPerGameHour = 0;
            var (result, failed) = DoctorFixes.Run(sapi, DoctorFixes.Id(DoctorFixes.Default), "toxicitydecaypergamehour", "en");
            if (failed) return "default: " + result;
            if (Math.Abs(Preservation.Config.ToxicityDecayPerGameHour - new EmbalmedEatsConfig().ToxicityDecayPerGameHour) > 0.0001f) return "default didn't put the value back";
            if (!result.Contains("ModData/embalmedeats/backups/")) return "the old file wasn't kept: " + result;

            Preservation.Config.AlwaysPreservable = new[] { "game:nosuchfood-*", "nosuchmod:thing", "game:redmeat-*" };
            (result, failed) = DoctorFixes.Run(sapi, DoctorFixes.DropUnmatched, "", "en");
            if (failed || !Preservation.Config.AlwaysPreservable.SequenceEqual(new[] { "nosuchmod:thing", "game:redmeat-*" })) return "drop-unmatched: " + result + " → " + string.Join(", ", Preservation.Config.AlwaysPreservable);

            Preservation.Config.EnableWaxDipping = false;
            (result, failed) = DoctorFixes.Run(sapi, DoctorFixes.MethodsOn, "", "en");
            if (failed || !Preservation.Config.EnableWaxDipping) return "methods-on: " + result;

            (result, failed) = DoctorFixes.Run(sapi, DoctorFixes.Cure, "all", "en");
            if (failed) return "cure: " + result;
            if (!DoctorFixes.Run(sapi, DoctorFixes.Default, "NoSuchSetting", "en").failed) return "an unknown setting didn't fail";
            if (!DoctorFixes.Run(sapi, "embalmedeats:nosuchfix", "", "en").failed) return "an unknown fix didn't fail";

            // The same through the doctor's event
            Preservation.Config.ToxicityPerItem = 0;
            var tree = new TreeAttribute();
            tree.SetString("id", DoctorFixes.Id(DoctorFixes.Default));
            tree.SetString("argument", "ToxicityPerItem");
            tree.SetString("side", "server");
            tree.SetString("language", "en");
            sapi.Event.PushEvent(DoctorBridge.FixEvent, tree);
            if (tree.GetBool("failed") || string.IsNullOrEmpty(tree.GetString("result"))) return "fix over the event bus: " + tree.GetString("result");
            if (Preservation.Config.ToxicityPerItem != new EmbalmedEatsConfig().ToxicityPerItem) return "fix over the event bus didn't change the setting";
            return Raw(new[] { result, tree.GetString("result") });
        }));

        check("Vintage Doctor: cards about one item", () =>
        {
            if (meat == null) return "no raw meat";
            ITreeAttribute r = About(sapi, "item:game:redmeat-raw", "", null);
            if (r["embalmedeats:about"] is not ITreeAttribute meatCard || !(meatCard.GetString("title") ?? "").StartsWith(Lang.Get("embalmedeats:doctor-about-yes", "").Split('{')[0].Trim())) return "raw meat: no 'can be preserved' card";
            if (!(meatCard.GetString("details") ?? "").Contains(Lang.Get("embalmedeats:method-wax").Substring(1))) return "raw meat: the methods aren't listed";
            if (About(sapi, "item:game:beeswax", "", null).Count != 0) return "a card for beeswax, which nobody would call food";
            if (world.GetItem(new AssetLocation("game:rot")) != null && About(sapi, "item:game:rot", "", null)["embalmedeats:about"] == null) return "no card saying rot can't be preserved (NeverPreservable)";

            var held = new ItemStack(meat, 1);
            Preservation.Apply(held, PreservationMethods.Wax);
            if (About(sapi, "item:game:redmeat-raw", "", held)["embalmedeats:about-held"] == null) return "no card about the preserved stack held";
            if (About(sapi, "item:game:redmeat-raw", "someplayeruid", null).Count != 0) return "the server answered for a player (their own game does)";
            return BadCards(About(sapi, "item:game:redmeat-raw", "", held));
        });

        check("Vintage Doctor: its page", () =>
        {
            var topics = new TreeAttribute();
            topics.SetString("side", "server");
            topics["topics"] = new TreeAttribute();
            sapi.Event.PushEvent(DoctorBridge.TopicsEvent, topics);
            if ((topics["topics"] as ITreeAttribute)?[DoctorBridge.TopicId] is not ITreeAttribute t) return "no page offered";
            if (!Lang.HasTranslation(t.GetString("question") ?? "")) return "the question has no text";

            string? bad = PageProblem(Page(sapi, ""), mustContain: Lang.Get("embalmedeats:doctor-page-food"));
            if (bad != null) return "whole page: " + bad;
            bad = PageProblem(Page(sapi, "item game:redmeat-raw"), mustContain: "[[item:game:redmeat-raw]]");
            if (bad != null) return "item page: " + bad;
            bad = PageProblem(Page(sapi, "item game:nosuchthing"), mustContain: "game:nosuchthing");
            if (bad != null) return "unknown item page: " + bad;
            return null;
        });

        check("Vintage Doctor: the player's game against the server", () =>
        {
            JObject server = ClientFacts.Build(world, "1.0", true);
            var same = new List<Finding>();
            ClientFacts.Compare("en", server, (JObject)server.DeepClone(), same);
            if (same.Count != 1 || same[0].Key != "sync" || same[0].Level != Level.Ok) return "the same facts didn't agree";

            var player = (JObject)server.DeepClone();
            player["creative"] = "1:0000";
            player["cfg"] = "other";
            player["got"] = false;
            player["foods"] = 3;
            var differ = new List<Finding>();
            ClientFacts.Compare("en", server, player, differ);
            foreach (string key in new[] { "sync-creative", "sync-settings", "sync-foods" })
            {
                if (!differ.Any(f => f.Key == key)) return "not found: " + key;
            }
            if (differ.First(f => f.Key == "sync-creative").Level != Level.Problem) return "different creative lists aren't a problem";
            if (differ.First(f => f.Key == "sync-settings").Title != Lang.Get("embalmedeats:doctor-sync-settings-missing")) return "settings that never arrived not told apart";

            // Over the event bus, as the doctor sends what the player's game said
            ITreeAttribute results = Collect(sapi, null, ClientFacts.ToBytes(server));
            if (results["embalmedeats:sync"] == null) return "the facts sent along weren't compared";
            if (!server["creative"]!.ToString().StartsWith(CreativeCount(world) + ":")) return "the creative fingerprint doesn't count the foods with tabs";
            return Raw(differ);
        });

        check("Vintage Doctor: words for every reason and part of the mod", () =>
        {
            foreach (FoodReason reason in Enum.GetValues(typeof(FoodReason)))
            {
                if (!Lang.HasTranslation("embalmedeats:doctor-reason-" + reason.ToString().ToLowerInvariant())) return "no text for reason " + reason;
            }
            foreach (string feature in ModChecks.Features.Append("other"))
            {
                if (!Lang.HasTranslation("embalmedeats:doctor-feature-" + feature)) return "no text for feature " + feature;
            }
            if (ModChecks.FeatureOf("display: BlockEntityDisplayCase.TryPut not found") != "display") return "feature of a failure wrong";
            if (ModChecks.FeatureOf("Method not found: X.Y") != "other") return "unknown feature not 'other'";
            return null;
        });

        check("Vintage Doctor: its JSON file", () =>
        {
            IAsset? asset = sapi.Assets.TryGet(new AssetLocation("embalmedeats", "config/vintagedoctor.json"));
            if (asset == null) return "assets/embalmedeats/config/vintagedoctor.json missing";
            JObject o = JObject.Parse(asset.ToText());
            if (o["configFiles"]?.Any(f => f.ToString() == EmbalmedEatsModSystem.ConfigFile) != true) return "configFiles doesn't list " + EmbalmedEatsModSystem.ConfigFile;
            foreach (JToken k in o["knownProblems"] ?? new JArray())
            {
                if (string.IsNullOrEmpty(k["match"]?.ToString()) || string.IsNullOrEmpty(k["title"]?.ToString())) return "a known problem without match or title";
            }
            return null;
        });

        check("/embalmedeats check", () =>
        {
            string text = Finding.PlainText(ModChecks.Server(sapi, "en", null), "en");
            if (!text.Contains("✓")) return "nothing passed: " + text;
            return text.Contains("embalmedeats:doctor-") ? "a text is missing: " + text : null;
        });
    }

    // ------------------------------------------------------------------ helpers

    static ITreeAttribute Collect(ICoreServerAPI sapi, string? onlyMod, byte[]? facts)
    {
        var tree = new TreeAttribute();
        tree.SetString("side", "server");
        tree.SetString("topic", onlyMod == null ? "overview" : "mod");
        tree.SetString("language", "en");
        tree.SetString("player", "");
        if (onlyMod != null) tree.SetString("mod", onlyMod);
        if (facts != null)
        {
            var cf = new TreeAttribute();
            cf.SetBytes(EmbalmedEatsModSystem.ModId, facts);
            tree["clientfacts"] = cf;
        }
        tree["results"] = new TreeAttribute();
        sapi.Event.PushEvent(DoctorBridge.CollectEvent, tree);
        return (ITreeAttribute)tree["results"];
    }

    static ITreeAttribute About(ICoreServerAPI sapi, string subject, string player, ItemStack? held)
    {
        var tree = new TreeAttribute();
        tree.SetString("side", "server");
        tree.SetString("topic", "item");
        tree.SetString("language", "en");
        tree.SetString("player", player);
        tree.SetString("subject", subject);
        if (held != null)
        {
            tree.SetBytes("held", held.ToBytes());
            tree.SetString("heldCode", "item:" + held.Collectible.Code);
        }
        tree["results"] = new TreeAttribute();
        sapi.Event.PushEvent(DoctorBridge.AboutEvent, tree);
        return (ITreeAttribute)tree["results"];
    }

    static TreeAttribute Page(ICoreServerAPI sapi, string argument)
    {
        var tree = new TreeAttribute();
        tree.SetString("side", "server");
        tree.SetString("topic", DoctorBridge.TopicId);
        tree.SetString("argument", argument);
        tree.SetString("language", "en");
        tree.SetString("player", "");
        tree.SetString("request", "selftest");
        sapi.Event.PushEvent(DoctorBridge.PageEvent, tree);
        return tree;
    }

    /// <summary>A page the doctor would show wrongly: lines and styles that don't line up, a raw text key, a button it can't run.</summary>
    static string? PageProblem(TreeAttribute tree, string mustContain)
    {
        string[]? texts = (tree["texts"] as StringArrayAttribute)?.value;
        byte[]? styles = tree.GetBytes("styles", null), indents = tree.GetBytes("indents", null);
        if (texts == null || texts.Length == 0) return "no lines";
        if (styles == null || indents == null || styles.Length != texts.Length || indents.Length != texts.Length) return "styles and indents don't match the lines";
        if (styles[0] != 1) return "doesn't start with a heading";
        if (styles.Any(s => s > 5)) return "unknown style";
        string all = string.Join("\n", texts);
        if (all.Contains("embalmedeats:doctor-")) return "a text is missing: " + texts.First(t => t.Contains("embalmedeats:doctor-"));
        if (!all.Contains(mustContain)) return "doesn't say " + mustContain;
        var fixes = new HashSet<string>(DoctorFixes.All.Select(DoctorFixes.Id));
        foreach (Match m in Token.Matches(all))
        {
            if (m.Groups[1].Value == "fix" && !fixes.Contains(m.Groups[2].Value)) return "a button for an unknown fix: " + m.Value;
            if (m.Groups[1].Value == "open" && !new[] { DoctorBridge.TopicId, "mod", "log" }.Contains(m.Groups[2].Value)) return "a button to an unknown page: " + m.Value;
        }
        if (all.Replace("{{", "").Length != all.Length && Token.Matches(all).Count != Regex.Matches(all, @"\{\{").Count) return "a button that isn't written right";
        return null;
    }

    /// <summary>Cards the doctor would show wrongly: no title, an unknown level, a raw text key.</summary>
    static string? BadCards(ITreeAttribute results)
    {
        foreach (var kv in results)
        {
            if (kv.Value is not ITreeAttribute r) continue;
            if (!kv.Key.StartsWith("embalmedeats:", StringComparison.Ordinal)) return "a card not named after the mod: " + kv.Key;
            if (string.IsNullOrWhiteSpace(r.GetString("title"))) return kv.Key + " has no title";
            if (!new[] { "ok", "info", "warning", "problem" }.Contains(r.GetString("level"))) return kv.Key + ": unknown level " + r.GetString("level");
            string all = r.GetString("title") + r.GetString("details") + r.GetString("advice") + r.GetString("fixLabel") + r.GetString("linkLabel");
            if (all.Contains("embalmedeats:doctor-")) return kv.Key + ": a text is missing: " + all;
        }
        return null;
    }

    static string? Raw(IEnumerable<Finding> findings) =>
        findings.Select(f => f.Title + string.Join("", f.Details) + f.Advice + f.FixLabel).FirstOrDefault(t => t.Contains("embalmedeats:doctor-")) is string bad ? "a text is missing: " + bad : null;

    static string? Raw(IEnumerable<string?> lines) =>
        lines.FirstOrDefault(t => t?.Contains("embalmedeats:doctor-") == true) is string bad ? "a text is missing: " + bad : null;

    static int CreativeCount(IWorldAccessor world) => world.Collectibles.Count(c => c?.Code != null && c.CreativeInventoryStacks != null
        && c.CreativeInventoryStacks.Any(l => l?.Tabs?.Any(Decoration.CreativeTabs.IsOurTab) == true));

    /// <summary>Runs a test with changed settings, then puts the settings (and the file) back as they were.</summary>
    static string? WithConfig(ICoreServerAPI sapi, Action<EmbalmedEatsConfig> change, Func<string?> test)
    {
        EmbalmedEatsConfig saved = Preservation.Config;
        var copy = JsonConvert.DeserializeObject<EmbalmedEatsConfig>(JsonConvert.SerializeObject(saved))!;
        try
        {
            Preservation.Config = copy;
            Preservation.ClearCache();
            change(copy);
            Preservation.ClearCache();
            return test();
        }
        finally
        {
            Preservation.Config = saved;
            Preservation.ClearCache();
            sapi.StoreModConfig(saved, EmbalmedEatsModSystem.ConfigFile);
        }
    }
}
