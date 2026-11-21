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
    /// <summary>This side's facts: the mod's version, whether the server's settings arrived, and fingerprints of what matters.</summary>
    public static JObject Build(IWorldAccessor world, string version, bool settingsReceived) => new()
    {
        ["v"] = version,
        ["got"] = settingsReceived,
        ["cfg"] = ConfigFingerprint(),
        ["creative"] = CreativeFingerprint(world),
        ["foods"] = world.Collectibles.Count(c => c?.Code != null && Preservation.IsPreservableCollectible(c)),
        ["ground"] = world.Items.Count(i => i?.Code != null && GroundStorageSupport.IsAddedFor(i))
    };

    public static byte[] ToBytes(JObject facts) => Encoding.UTF8.GetBytes(facts.ToString(Formatting.None));

    public static JObject? FromBytes(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try { return JObject.Parse(Encoding.UTF8.GetString(bytes)); }
        catch { return null; }
    }

    public static string ConfigFingerprint() => Hash(JsonConvert.SerializeObject(Preservation.Config));

    /// <summary>
    /// Every creative list that has one of our tabs, in order: each list's tabs and, for each stack, its code and its
    /// preservation method. Codes and methods only, so data that is the same but written differently doesn't count.
    /// </summary>
    public static string CreativeFingerprint(IWorldAccessor world)
    {
        var sb = new StringBuilder();
        int foods = 0;
        IEnumerable<CollectibleObject> withTabs = world.Collectibles
            .Where(c => c?.Code != null && c.CreativeInventoryStacks != null && c.CreativeInventoryStacks.Any(l => l?.Tabs?.Any(CreativeTabs.IsOurTab) == true))
            .OrderBy(c => c.Code.ToString(), StringComparer.Ordinal);
        foreach (CollectibleObject coll in withTabs)
        {
            foods++;
            sb.Append(coll.Code).Append('=');
            foreach (var list in coll.CreativeInventoryStacks)
            {
                sb.Append(string.Join(",", list?.Tabs ?? Array.Empty<string>())).Append(':');
                foreach (var j in list?.Stacks ?? Array.Empty<JsonItemStack>())
                {
                    ItemStack? stack = j?.ResolvedItemstack;
                    sb.Append(stack?.Collectible?.Code?.ToString() ?? "?").Append('/').Append(Preservation.GetMethod(stack) ?? "-").Append(';');
                }
                sb.Append('|');
            }
            sb.Append('\n');
        }
        return foods + ":" + Hash(sb.ToString());
    }

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
        string version = sapi.ModLoader.GetModSystem<EmbalmedEatsModSystem>()?.Mod?.Info?.Version ?? "?";
        JObject ours = Build(sapi.World, version, true);
        Compare(lang, ours, theirs, list);
    }

    /// <summary>Compares two sets of facts (the server's, a player's). Split out so the self-test can try every case.</summary>
    public static void Compare(string lang, JObject server, JObject player, List<Finding> list)
    {
        string S(JObject o, string key) => o[key]?.ToString() ?? "";
        int before = list.Count;

        if (S(server, "creative") != S(player, "creative"))
        {
            list.Add(new Finding("sync-creative", Level.Problem, DoctorText.T(lang, "sync-creative"))
                .Detail(DoctorText.T(lang, "sync-versions", S(player, "v"), S(server, "v")))
                .WithAdvice(DoctorText.T(lang, "sync-creative-advice")));
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
}
