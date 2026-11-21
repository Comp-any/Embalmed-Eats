using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using EmbalmedEats.Core;
using EmbalmedEats.Decoration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace EmbalmedEats.Doctor;

/// <summary>
/// What Embalmed Eats on a player's game knows, sent to the server with each Vintage Doctor request
/// ("vintagedoctor:clientfacts"), so the server can tell whether the two agree. The two must agree: the server hands
/// out creative items by their place in its own lists (a difference gives players the wrong item, the 1.1.0 bug),
/// and a player's game that doesn't have the server's settings shows and refuses the wrong things.
/// </summary>
public static class ClientFacts
{
    /// <summary>
    /// The facts' format. A player's game with another format (another version of Embalmed Eats) isn't compared on
    /// the creative tabs: the fingerprints wouldn't mean the same thing.
    /// </summary>
    public const int Format = 2;

    /// <summary>This side's facts: the mod's version, whether the server's settings arrived, and fingerprints of what matters.</summary>
    /// <param name="addedHere">How many foods this side gave the creative tabs itself (a player's game should need none: they come with the server's items).</param>
    public static JObject Build(IWorldAccessor world, string version, bool settingsReceived, int addedHere = 0)
    {
        Creative creative = CreativeLists(world);
        return new JObject
        {
            ["fv"] = Format,
            ["v"] = version,
            ["got"] = settingsReceived,
            ["cfg"] = ConfigFingerprint(),
            ["creative"] = creative.Fingerprint,
            ["tabs"] = new JObject(creative.Tabs.Select(t => new JProperty(t.Key, t.Value))),
            ["withTabs"] = new JArray(creative.Foods),
            ["unresolved"] = creative.Unresolved,
            ["addedHere"] = addedHere,
            ["foods"] = world.Collectibles.Count(c => c?.Code != null && Preservation.IsPreservableCollectible(c)),
            ["ground"] = world.Items.Count(i => i?.Code != null && GroundStorageSupport.IsAddedFor(i))
        };
    }

    public static byte[] ToBytes(JObject facts) => Encoding.UTF8.GetBytes(facts.ToString(Formatting.None));

    public static JObject? FromBytes(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try { return JObject.Parse(Encoding.UTF8.GetString(bytes)); }
        catch { return null; }
    }

    public static string ConfigFingerprint() => Hash(JsonConvert.SerializeObject(Preservation.Config));

    /// <summary>What the creative inventory shows of the foods that have our tabs, as both sides can compare it.</summary>
    public class Creative
    {
        /// <summary>"&lt;foods&gt;:&lt;hash of every tab&gt;".</summary>
        public string Fingerprint = "";
        /// <summary>Each tab those foods are in → "&lt;stacks&gt;:&lt;hash&gt;".</summary>
        public SortedDictionary<string, string> Tabs = new(StringComparer.Ordinal);
        /// <summary>The foods that have our tabs, by code.</summary>
        public List<string> Foods = new();
        /// <summary>Stacks in those lists that this side couldn't turn into an item (counted once per tab they show in).</summary>
        public int Unresolved;
    }

    /// <summary>
    /// For every food that has one of our tabs (in code order), what each tab it is in shows: per tab, the stacks in
    /// order, each as its code and its preservation method. Per tab rather than per list, because that is what the
    /// creative inventory shows and what its slots point at; how the entries are grouped into lists doesn't matter.
    /// A stack that isn't resolved on this side is taken from its code and attributes, so that alone isn't a difference.
    /// </summary>
    /// <param name="listsOf">Whose lists to read (for the self-test's copy of what a player's game receives); each food's own by default.</param>
    public static Creative CreativeLists(IWorldAccessor world, System.Func<CollectibleObject, CreativeTabAndStackList[]?>? listsOf = null)
    {
        listsOf ??= c => c.CreativeInventoryStacks;
        var result = new Creative();
        var perTab = new SortedDictionary<string, (int count, StringBuilder text)>(StringComparer.Ordinal);
        IEnumerable<CollectibleObject> withTabs = world.Collectibles
            .Where(c => c?.Code != null && listsOf(c)?.Any(l => l?.Tabs?.Any(CreativeTabs.IsOurTab) == true) == true)
            .OrderBy(c => c.Code.ToString(), StringComparer.Ordinal);
        foreach (CollectibleObject coll in withTabs)
        {
            result.Foods.Add(coll.Code.ToString());
            foreach (CreativeTabAndStackList? list in listsOf(coll)!)
            {
                if (list?.Tabs == null || list.Stacks == null) continue;
                foreach (string tab in list.Tabs)
                {
                    if (tab == null) continue;
                    if (!perTab.TryGetValue(tab, out var entry)) entry = (0, new StringBuilder());
                    foreach (JsonItemStack? j in list.Stacks)
                    {
                        entry.text.Append(StackIdentity(world, j, ref result.Unresolved)).Append(';');
                        entry.count++;
                    }
                    perTab[tab] = entry;
                }
            }
        }
        var all = new StringBuilder();
        foreach (var (tab, entry) in perTab)
        {
            string summary = entry.count + ":" + Hash(entry.text.ToString());
            result.Tabs[tab] = summary;
            all.Append(tab).Append('=').Append(summary).Append('\n');
        }
        result.Fingerprint = result.Foods.Count + ":" + Hash(all.ToString());
        return result;
    }

    /// <summary>
    /// "game:bread-spelt-perfect/wax": the stack's code and preservation method (or "-"). On a player's game the
    /// stacks that came with the server's items know only their item's number until the game looks them up (the
    /// 1.2.0 card said every list differed in single player because of that), so the item is looked up by number here.
    /// </summary>
    static string StackIdentity(IWorldAccessor world, JsonItemStack? j, ref int unresolved)
    {
        ItemStack? stack = j?.ResolvedItemstack;
        if (stack != null)
        {
            CollectibleObject? coll = stack.Collectible ?? (stack.Class == EnumItemClass.Block ? world.GetBlock(stack.Id) : world.GetItem(stack.Id));
            if (coll?.Code != null) return coll.Code + "/" + (Preservation.GetMethod(stack) ?? "-");
        }
        unresolved++;
        string code = j?.Code?.ToString() ?? "?";
        string? method = null;
        try { method = j?.Attributes?["embalmedeats"]?["method"]?.AsString(); } catch { }
        return code + "/" + (string.IsNullOrEmpty(method) ? "-" : method);
    }

    /// <summary>The old name: the overall creative fingerprint.</summary>
    public static string CreativeFingerprint(IWorldAccessor world) => CreativeLists(world).Fingerprint;

    public static string Hash(string text)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
    }

    /// <summary>The server's side of it: compares a player's facts with its own and adds what differs (or that all agrees).</summary>
    public static void Compare(ICoreServerAPI sapi, string lang, byte[] bytes, List<Finding> list)
    {
        JObject? theirs = FromBytes(bytes);
        if (theirs == null) return;
        var mod = sapi.ModLoader.GetModSystem<EmbalmedEatsModSystem>();
        JObject ours = Build(sapi.World, mod?.Mod?.Info?.Version ?? "?", true, mod?.CreativeAddedHere ?? 0);
        Compare(lang, ours, theirs, list);
    }

    /// <summary>Compares two sets of facts (the server's, a player's). Split out so the self-test can try every case.</summary>
    public static void Compare(string lang, JObject server, JObject player, List<Finding> list)
    {
        string S(JObject o, string key) => o[key]?.ToString() ?? "";
        int before = list.Count;

        if (S(server, "fv") != S(player, "fv"))
        {
            // Another version of Embalmed Eats on the player's game: its creative fingerprint is worked out differently
            list.Add(new Finding("sync-format", Level.Info, DoctorText.T(lang, "sync-format"))
                .Detail(DoctorText.T(lang, "sync-versions", S(player, "v"), S(server, "v")))
                .WithAdvice(DoctorText.T(lang, "sync-format-advice")));
        }
        else if (S(server, "creative") != S(player, "creative"))
        {
            var f = new Finding("sync-creative", Level.Problem, DoctorText.T(lang, "sync-creative"))
                .Detail(DoctorText.T(lang, "sync-versions", S(player, "v"), S(server, "v")));
            foreach (string line in CreativeDifferences(lang, server, player)) f.Detail(line);
            list.Add(f.WithAdvice(DoctorText.T(lang, "sync-creative-advice")));
        }

        if (S(server, "cfg") != S(player, "cfg"))
        {
            bool got = player["got"]?.Type == JTokenType.Boolean && (bool)player["got"]!;
            list.Add(new Finding("sync-settings", Level.Warning, DoctorText.T(lang, got ? "sync-settings-differ" : "sync-settings-missing"))
                .WithAdvice(DoctorText.T(lang, "sync-settings-advice")));
        }

        if (S(server, "foods") != S(player, "foods") || S(server, "ground") != S(player, "ground"))
        {
            list.Add(new Finding("sync-foods", Level.Warning, DoctorText.T(lang, "sync-foods"))
                .Detail(DoctorText.T(lang, "sync-foods-detail", S(player, "foods"), S(server, "foods")))
                .Detail(DoctorText.T(lang, "sync-ground-detail", S(player, "ground"), S(server, "ground")))
                .WithAdvice(DoctorText.T(lang, "sync-foods-advice")));
        }

        if (list.Count == before)
        {
            list.Add(new Finding("sync", Level.Ok, DoctorText.T(lang, "sync-ok"))
                .Detail(DoctorText.T(lang, "sync-ok-detail", S(server, "foods"), S(server, "creative").Split(':')[0])));
        }
    }

    /// <summary>What differs between the two sides' creative tabs, for the card: foods only one side has in them, tabs with a different number of entries, stacks a side couldn't read.</summary>
    public static List<string> CreativeDifferences(string lang, JObject server, JObject player)
    {
        var lines = new List<string>();
        HashSet<string> Codes(JObject o) => new((o["withTabs"] as JArray)?.Select(t => t.ToString()) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        string Few(IEnumerable<string> items)
        {
            var all = items.OrderBy(x => x, StringComparer.Ordinal).ToList();
            return all.Count <= 8 ? string.Join(", ", all) : string.Join(", ", all.Take(8)) + " " + DoctorText.T(lang, "and-more", all.Count - 8);
        }

        HashSet<string> mine = Codes(server), theirs = Codes(player);
        var onlyPlayer = theirs.Where(c => !mine.Contains(c)).ToList();
        var onlyServer = mine.Where(c => !theirs.Contains(c)).ToList();
        if (onlyPlayer.Count > 0) lines.Add(DoctorText.T(lang, "sync-creative-only-player", onlyPlayer.Count, Few(onlyPlayer)));
        if (onlyServer.Count > 0) lines.Add(DoctorText.T(lang, "sync-creative-only-server", onlyServer.Count, Few(onlyServer)));

        var serverTabs = server["tabs"] as JObject ?? new JObject();
        var playerTabs = player["tabs"] as JObject ?? new JObject();
        int shown = 0, sameCountDifferent = 0;
        foreach (string tab in serverTabs.Properties().Select(p => p.Name).Union(playerTabs.Properties().Select(p => p.Name)).OrderBy(t => t, StringComparer.Ordinal))
        {
            string a = playerTabs[tab]?.ToString() ?? "0:-", b = serverTabs[tab]?.ToString() ?? "0:-";
            if (a == b) continue;
            string countA = a.Split(':')[0], countB = b.Split(':')[0];
            if (countA == countB) { sameCountDifferent++; continue; }
            if (shown++ < 6) lines.Add(DoctorText.T(lang, "sync-creative-tab", tab, countA, countB));
        }
        if (sameCountDifferent > 0) lines.Add(DoctorText.T(lang, "sync-creative-order", sameCountDifferent));

        int unresolved = player["unresolved"]?.Type == JTokenType.Integer ? (int)player["unresolved"]! : 0;
        if (unresolved > 0) lines.Add(DoctorText.T(lang, "sync-creative-unresolved", unresolved));
        int addedHere = player["addedHere"]?.Type == JTokenType.Integer ? (int)player["addedHere"]! : 0;
        if (addedHere > 0) lines.Add(DoctorText.T(lang, "sync-creative-added-here", addedHere));
        return lines;
    }
}
