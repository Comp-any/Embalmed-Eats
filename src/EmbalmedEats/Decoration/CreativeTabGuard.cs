using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace EmbalmedEats.Decoration;

/// <summary>
/// Keeps the creative lists of the foods we preserve, on a player's game, exactly as the server sent them.
///
/// The server hands out creative items by their place in its own lists, so a mod that changes those lists only on
/// the player's game makes the creative inventory give the wrong item. Vintage Creation before 0.44.1 did: it added
/// its tab to every list of its blocks and items on the player's game too, which included our preserved lists (they
/// arrive from the server without it, because we add them after Vintage Creation runs there). Its tab then had 25
/// entries on the player's game and 5 on the server.
///
/// The lists arrive with the server's items before any mod's AssetsFinalize runs on the player's game.
/// <see cref="CreativeTabCopySystem"/> copies the tabs of every list of each food that has one of ours, before any
/// other mod; <see cref="CreativeTabRestoreSystem"/> puts back any that changed, after every other mod, and again when
/// the block textures are loaded and when the world is ready. Only the tabs are put back, and only on lists that came
/// from the server: lists a mod adds on the player's game are left alone (Vintage Doctor's comparison still reports them).
/// </summary>
public static class CreativeTabGuard
{
    /// <summary>Before every other mod's AssetsFinalize (most use 0 to 10).</summary>
    public const double CopyOrder = -1000;

    /// <summary>After every other mod's AssetsFinalize.</summary>
    public const double RestoreOrder = 1000;

    /// <summary>The tabs of every list of the foods that have one of ours, as they were when copied.</summary>
    public sealed class Saved
    {
        internal readonly List<(CollectibleObject coll, CreativeTabAndStackList list, string[]? tabs)> Lists = new();

        /// <summary>How many lists were copied.</summary>
        public int Count => Lists.Count;

        /// <summary>How many foods they belong to.</summary>
        public int Foods => Lists.Select(l => l.coll).Distinct().Count();
    }

    /// <summary>What was put back: how many lists, and which tabs another mod had added or taken away.</summary>
    public sealed class Restored
    {
        public int Lists;
        public readonly SortedSet<string> Added = new(StringComparer.Ordinal);
        public readonly SortedSet<string> Removed = new(StringComparer.Ordinal);

        public void Add(Restored other)
        {
            Lists += other.Lists;
            Added.UnionWith(other.Added);
            Removed.UnionWith(other.Removed);
        }
    }

    /// <summary>Copies the tabs of every list of each food that has one of our lists.</summary>
    public static Saved Copy(IEnumerable<CollectibleObject?> collectibles)
    {
        var saved = new Saved();
        foreach (CollectibleObject? coll in collectibles)
        {
            CreativeTabAndStackList[]? lists = coll?.CreativeInventoryStacks;
            if (coll == null || lists == null || !lists.Any(l => l?.Tabs?.Any(CreativeTabs.IsOurTab) == true)) continue;
            foreach (CreativeTabAndStackList? list in lists)
            {
                if (list != null) saved.Lists.Add((coll, list, list.Tabs?.ToArray()));
            }
        }
        return saved;
    }

    /// <summary>Puts back the copied tabs on every list whose tabs changed since. Safe to run more than once.</summary>
    public static Restored Restore(Saved saved)
    {
        var restored = new Restored();
        foreach (var (coll, list, tabs) in saved.Lists)
        {
            if (SameTabs(list.Tabs, tabs)) continue;
            // A list the food no longer has (a mod gave it new lists) isn't shown any more: nothing to put back there
            if (coll.CreativeInventoryStacks == null || Array.IndexOf(coll.CreativeInventoryStacks, list) < 0) continue;
            foreach (string tab in list.Tabs ?? Array.Empty<string>())
            {
                if (tab != null && tabs?.Contains(tab) != true) restored.Added.Add(tab);
            }
            foreach (string tab in tabs ?? Array.Empty<string>())
            {
                if (tab != null && list.Tabs?.Contains(tab) != true) restored.Removed.Add(tab);
            }
            list.Tabs = tabs?.ToArray()!;
            restored.Lists++;
        }
        return restored;
    }

    static bool SameTabs(string[]? a, string[]? b)
    {
        if (a == null || a.Length == 0) return b == null || b.Length == 0;
        return b != null && a.SequenceEqual(b, StringComparer.Ordinal);
    }
}

/// <summary>Copies the server's creative lists on a player's game, before any other mod can change them.</summary>
public class CreativeTabCopySystem : ModSystem
{
    CreativeTabGuard.Saved? saved;

    /// <summary>Everything put back so far this session (for Vintage Doctor's comparison).</summary>
    public readonly CreativeTabGuard.Restored Total = new();

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override double ExecuteOrder() => CreativeTabGuard.CopyOrder;

    public override void StartClientSide(ICoreClientAPI capi)
    {
        capi.Event.BlockTexturesLoaded += () => PutBack(capi);
        capi.Event.LevelFinalize += () => PutBack(capi);
    }

    public override void AssetsFinalize(ICoreAPI api)
    {
        try
        {
            saved = CreativeTabGuard.Copy(api.World.Collectibles);
            api.Logger.Notification("[Embalmed Eats] Creative lists of {0} foods kept as the server sent them ({1} lists).", saved.Foods, saved.Count);
        }
        catch (Exception e)
        {
            api.Logger.Warning("[Embalmed Eats] Couldn't copy the server's creative lists: {0}", e.Message);
        }
    }

    /// <summary>Puts back what another mod changed on this game only, and says so in the log.</summary>
    public void PutBack(ICoreAPI api)
    {
        if (saved == null) return;
        try
        {
            CreativeTabGuard.Restored now = CreativeTabGuard.Restore(saved);
            if (now.Lists == 0) return;
            Total.Add(now);
            api.Logger.Warning("[Embalmed Eats] Another mod changed the creative tabs of {0} lists of preserved foods on this game only (tabs added: {1}; taken away: {2}). Put them back as the server sent them, so the creative inventory gives the right items.",
                now.Lists, Names(now.Added), Names(now.Removed));
        }
        catch (Exception e)
        {
            api.Logger.Warning("[Embalmed Eats] Couldn't put back the server's creative lists: {0}", e.Message);
        }
    }

    static string Names(IEnumerable<string> tabs) => tabs.Any() ? string.Join(", ", tabs) : "-";

    public override void Dispose() => saved = null;
}

/// <summary>Puts back the server's creative lists after every other mod's AssetsFinalize on a player's game.</summary>
public class CreativeTabRestoreSystem : ModSystem
{
    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override double ExecuteOrder() => CreativeTabGuard.RestoreOrder;

    public override void AssetsFinalize(ICoreAPI api) => api.ModLoader.GetModSystem<CreativeTabCopySystem>()?.PutBack(api);
}
