using System;
using System.Collections.Generic;
using EmbalmedEats.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace EmbalmedEats.Doctor;

/// <summary>
/// Hands Vintage Doctor our checks, cards, page and fixes over its event bus (docs/api.md of Vintage Doctor), so
/// Embalmed Eats needs no reference to the doctor and loses nothing without it: if the doctor isn't installed,
/// nobody pushes these events and the listeners never run.
///
/// Which side answers what, so a single player game (where both halves show in one window) says each thing once:
/// - the server: the check-up cards about how the mod set itself up, our page, the fixes, and the comparison with
///   the player's game (from the facts that game sends along);
/// - the player's game: their own poisoning, and in multiplayer whether the server's settings arrived; the cards
///   on the doctor's pages about one item or block (every player sees those, admin or not). The server answers
///   those only for its console.
/// </summary>
public static class DoctorBridge
{
    public const string CollectEvent = "vintagedoctor:collect";
    public const string AboutEvent = "vintagedoctor:about";
    public const string TopicsEvent = "vintagedoctor:topics";
    public const string PageEvent = "vintagedoctor:page";
    public const string FixEvent = "vintagedoctor:fix";
    public const string ClientFactsEvent = "vintagedoctor:clientfacts";

    /// <summary>Our page: /doctor embalmedeats.</summary>
    public const string TopicId = "embalmedeats";

    public static void RegisterServer(ICoreServerAPI sapi)
    {
        sapi.Event.RegisterEventBusListener((string n, ref EnumHandling h, IAttribute d) => Safe(sapi, n, () => ServerCollect(sapi, d)), 0.5, CollectEvent);
        sapi.Event.RegisterEventBusListener((string n, ref EnumHandling h, IAttribute d) => Safe(sapi, n, () => ServerAbout(sapi, d)), 0.5, AboutEvent);
        sapi.Event.RegisterEventBusListener((string n, ref EnumHandling h, IAttribute d) => Safe(sapi, n, () => Topics(d, "server")), 0.5, TopicsEvent);
        sapi.Event.RegisterEventBusListener((string n, ref EnumHandling h, IAttribute d) => Safe(sapi, n, () => Page(sapi, d)), 0.5, PageEvent);
        sapi.Event.RegisterEventBusListener((string n, ref EnumHandling h, IAttribute d) => Safe(sapi, n, () => Fix(sapi, d)), 0.5, FixEvent);
    }

    public static void RegisterClient(ICoreClientAPI capi, string version)
    {
        capi.Event.RegisterEventBusListener((string n, ref EnumHandling h, IAttribute d) => Safe(capi, n, () => ClientFactsAsked(capi, d, version)), 0.5, ClientFactsEvent);
        capi.Event.RegisterEventBusListener((string n, ref EnumHandling h, IAttribute d) => Safe(capi, n, () => ClientCollect(capi, d)), 0.5, CollectEvent);
        capi.Event.RegisterEventBusListener((string n, ref EnumHandling h, IAttribute d) => Safe(capi, n, () => ClientAbout(capi, d)), 0.5, AboutEvent);
    }

    /// <summary>A mistake of ours must not break the doctor's page for every other mod.</summary>
    static void Safe(ICoreAPI api, string eventName, Action action)
    {
        try { action(); }
        catch (Exception e) { api.Logger.Warning("[Embalmed Eats] Answering Vintage Doctor ({0}) failed: {1}", eventName, e); }
    }

    static string Lang(ITreeAttribute tree) => tree.GetString("language") ?? "en";

    static bool ForUs(ITreeAttribute tree)
    {
        string? only = tree.GetString("mod");
        return string.IsNullOrEmpty(only) || only == EmbalmedEatsModSystem.ModId;
    }

    static byte[]? Facts(ITreeAttribute tree) => (tree["clientfacts"] as ITreeAttribute)?.GetBytes(EmbalmedEatsModSystem.ModId, null);

    static void Write(ITreeAttribute tree, IEnumerable<Finding> findings)
    {
        ITreeAttribute results = tree.GetOrAddTreeAttribute("results");
        foreach (Finding f in findings) f.WriteTo(results);
    }

    // ------------------------------------------------------------------ the server

    static void ServerCollect(ICoreServerAPI sapi, IAttribute data)
    {
        if (data is not ITreeAttribute tree || tree.GetString("side") != "server" || !ForUs(tree)) return;
        Write(tree, ModChecks.Server(sapi, Lang(tree), Facts(tree)));
    }

    static void ServerAbout(ICoreServerAPI sapi, IAttribute data)
    {
        if (data is not ITreeAttribute tree || tree.GetString("side") != "server") return;
        if (!string.IsNullOrEmpty(tree.GetString("player"))) return; // a player's own game answers for them
        CollectibleObject? coll = DoctorPage.Find(sapi.World, tree.GetString("subject") ?? "");
        if (coll == null) return;
        Write(tree, FoodCards.About(sapi.World, Lang(tree), coll, Held(sapi.World, tree)));
    }

    static void Topics(IAttribute data, string side)
    {
        if (data is not ITreeAttribute tree || tree.GetString("side") != side) return;
        ITreeAttribute page = tree.GetOrAddTreeAttribute("topics").GetOrAddTreeAttribute(TopicId);
        page.SetString("mod", EmbalmedEatsModSystem.ModId);
        page.SetString("question", "embalmedeats:doctor-question"); // a lang key: the doctor shows it in the asker's language
        page.SetBool("ownHeadings", true);
    }

    static void Page(ICoreServerAPI sapi, IAttribute data)
    {
        if (data is not ITreeAttribute tree || tree.GetString("topic") != TopicId) return;
        if (tree.GetString("side") is string side && side.Length > 0 && side != "server") return;
        DoctorPage.Build(sapi, Lang(tree), tree.GetString("argument") ?? "", Facts(tree), tree.GetString("heldCode")).WriteTo(tree);
    }

    static void Fix(ICoreServerAPI sapi, IAttribute data)
    {
        if (data is not ITreeAttribute tree) return;
        string? id = tree.GetString("id");
        if (id == null || !id.StartsWith(EmbalmedEatsModSystem.ModId + ":", StringComparison.Ordinal)) return;
        var (result, failed) = DoctorFixes.Run(sapi, id, tree.GetString("argument") ?? "", Lang(tree));
        sapi.Logger.Notification("[Embalmed Eats] Vintage Doctor fix {0} ({1}): {2}", id, tree.GetString("argument") ?? "", result);
        tree.SetString("result", result);
        if (failed) tree.SetBool("failed", true);
    }

    /// <summary>The stack the asker holds, when the doctor sent it along.</summary>
    static ItemStack? Held(IWorldAccessor world, ITreeAttribute tree)
    {
        byte[]? bytes = tree.GetBytes("held", null);
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            var stack = new ItemStack(bytes);
            return stack.ResolveBlockOrItem(world) ? stack : null;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ the player's game

    static void ClientFactsAsked(ICoreClientAPI capi, IAttribute data, string version)
    {
        if (data is not ITreeAttribute tree || tree["facts"] is not ITreeAttribute facts) return;
        bool got = capi.IsSinglePlayer || EmbalmedEatsModSystem.ConfigReceived;
        int addedHere = capi.ModLoader.GetModSystem<EmbalmedEatsModSystem>()?.CreativeAddedHere ?? 0;
        facts.SetBytes(EmbalmedEatsModSystem.ModId, ClientFacts.ToBytes(ClientFacts.Build(capi.World, version, got, addedHere)));
    }

    static void ClientCollect(ICoreClientAPI capi, IAttribute data)
    {
        if (data is not ITreeAttribute tree || tree.GetString("side") != "client" || !ForUs(tree)) return;
        Write(tree, ModChecks.Client(capi, Lang(tree)));
    }

    static void ClientAbout(ICoreClientAPI capi, IAttribute data)
    {
        if (data is not ITreeAttribute tree || tree.GetString("side") != "client") return;
        CollectibleObject? coll = DoctorPage.Find(capi.World, tree.GetString("subject") ?? "");
        if (coll == null) return;
        Write(tree, FoodCards.About(capi.World, Lang(tree), coll, Held(capi.World, tree)));
    }
}
