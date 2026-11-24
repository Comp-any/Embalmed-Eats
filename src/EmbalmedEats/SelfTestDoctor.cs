using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using EmbalmedEats.Config;
using EmbalmedEats.Core;
using EmbalmedEats.Decoration;
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
        // The fixes below change the settings file; their copies of it go to a temporary folder, not ModData
        DoctorFixes.BackupFolderOverride = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "embalmedeats-selftest");
        try { RunChecks(sapi, check); }
        finally { DoctorFixes.BackupFolderOverride = null; }
    }

    static void RunChecks(ICoreServerAPI sapi, Action<string, Func<string?>> check)
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
            if (!result.Contains("embalmedeats-selftest")) return "the old file wasn't kept: " + result;

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

            // Lists another mod changed on the player's game and Embalmed Eats put back: they agree, and the card says so
            var putBack = new CreativeTabGuard.Restored { Lists = 20 };
            putBack.Added.Add("vintagecreation");
            JObject mended = ClientFacts.Build(world, "1.0", true, 0, putBack);
            if (mended["putBack"]?.ToString() != "20") return "what was put back isn't in the facts";
            var told = new List<Finding>();
            ClientFacts.Compare("en", server, mended, told);
            string Cards() => string.Join(" / ", told.Select(f => f.Key + "=" + f.Level));
            if (!told.Any(f => f.Key == "sync" && f.Level == Level.Ok)) return "lists that were put back don't agree: " + Cards();
            Finding? putBackCard = told.FirstOrDefault(f => f.Key == "sync-putback");
            if (putBackCard == null || putBackCard.Level != Level.Info) return "lists that were put back aren't told: " + Cards();
            string putBackText = Finding.PlainText(new[] { putBackCard }, "en");
            if (!putBackText.Contains("vintagecreation")) return "the card doesn't name the tab that was added: " + putBackText;
            if (Raw(told) is string missing) return missing;

            // Over the event bus, as the doctor sends what the player's game said
            ITreeAttribute results = Collect(sapi, null, ClientFacts.ToBytes(server));
            if (results["embalmedeats:sync"] == null) return "the facts sent along weren't compared";
            if (!server["creative"]!.ToString().StartsWith(CreativeCount(world) + ":")) return "the creative fingerprint doesn't count the foods with tabs";
            return Raw(differ);
        });

        check("Vintage Doctor: the creative tabs compare the same after the trip to a player's game", () =>
        {
            // What a player's game gets: each list's tabs, and each stack as its bytes (item number, attributes) and
            // JSON, not yet looked up (JsonItemStack.FromBytes). In 1.2.0 that alone made every list look different.
            int unlooked = 0;
            CreativeTabAndStackList[]? Received(CollectibleObject coll) => coll.CreativeInventoryStacks?.Select(list => list == null ? null! : new CreativeTabAndStackList
            {
                Tabs = list.Tabs?.ToArray()!,
                Stacks = list.Stacks?.Select(j =>
                {
                    if (j == null) return null!;
                    var copy = new JsonItemStack
                    {
                        Type = j.Type,
                        Code = new AssetLocation(j.Code.ToShortString()),
                        StackSize = j.StackSize,
                        Attributes = j.Attributes == null ? null : new JsonObject(JToken.Parse(j.Attributes.ToString())),
                        ResolvedItemstack = j.ResolvedItemstack == null ? null : new ItemStack(j.ResolvedItemstack.ToBytes())
                    };
                    if (copy.ResolvedItemstack != null && copy.ResolvedItemstack.Collectible == null) unlooked++;
                    return copy;
                }).ToArray()!
            }).ToArray();

            var here = ClientFacts.CreativeLists(world);
            var there = ClientFacts.CreativeLists(world, Received);
            if (here.Foods.Count == 0) return "no food has the tabs";
            if (unlooked == 0) return "the copy didn't reproduce stacks that aren't looked up yet";
            if (there.Unresolved != 0) return there.Unresolved + " stacks couldn't be identified";
            if (here.Fingerprint != there.Fingerprint)
            {
                string tab = here.Tabs.Keys.FirstOrDefault(t => !there.Tabs.TryGetValue(t, out var v) || v != here.Tabs[t]) ?? "?";
                return "differ after the trip (first tab: " + tab + ")";
            }
            // And a real difference is still found, and said where
            var changed = ClientFacts.Build(world, "1.0", true);
            changed["tabs"]![CreativeTabs.TabFor(PreservationMethods.Wax)] = "1:x";
            changed["creative"] = "1:x";
            var lines = ClientFacts.CreativeDifferences("en", ClientFacts.Build(world, "1.0", true), changed);
            if (!lines.Any(l => l.Contains(CreativeTabs.TabFor(PreservationMethods.Wax)))) return "a tab that differs isn't named: " + string.Join(" / ", lines);
            return null;
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
            foreach (string feature in ModChecks.Features)
            {
                if (!Lang.HasTranslation("embalmedeats:doctor-part-" + feature)) return "no text for part " + feature;
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
