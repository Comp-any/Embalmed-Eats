using System;
using System.Linq;
using EmbalmedEats.Config;
using EmbalmedEats.Core;
using EmbalmedEats.Decoration;
using EmbalmedEats.Doctor;
using EmbalmedEats.Patches;
using EmbalmedEats.Recipes;
using EmbalmedEats.Toxicity;
using HarmonyLib;
using Newtonsoft.Json;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace EmbalmedEats;

[ProtoContract]
public class ConfigSyncPacket
{
    [ProtoMember(1)] public string Json = "";
}

public class EmbalmedEatsModSystem : ModSystem
{
    public const string ModId = "embalmedeats";
    public const string ConfigFile = "embalmedeats.json";

    /// <summary>On a player's game: the server's settings have arrived (always true in single player).</summary>
    public static bool ConfigReceived;
    /// <summary>Why the settings file couldn't be read at this start, and where the unreadable file was kept (for the checks).</summary>
    public static string? ConfigLoadError, ConfigBackup;

    static readonly object patchLock = new();
    static Harmony? harmony;
    static int patchUsers;

    ICoreAPI api = null!;
    ICoreServerAPI? sapi;
    IServerNetworkChannel? serverChannel;
    ToxicitySystem? toxicity;

    // Late, so other mods have finished adjusting their food before we generate recipes
    public override double ExecuteOrder() => 5;

    public override void StartPre(ICoreAPI api)
    {
        this.api = api;
        LoadConfig(api);
    }

    public override void Start(ICoreAPI api)
    {
        PatchUtil.Logger = api.Logger;

        // Client and server share one process in singleplayer, Harmony patches are process wide
        lock (patchLock)
        {
            if (harmony == null)
            {
                harmony = new Harmony(ModId);
                ApplyPatches(harmony, api);
            }
            patchUsers++;
        }

        api.Network.RegisterChannel(ModId).RegisterMessageType<ConfigSyncPacket>();
    }

    static void ApplyPatches(Harmony harmony, ICoreAPI api)
    {
        void Run(string name, Action<Harmony> apply)
        {
            try { apply(harmony); }
            catch (Exception e)
            {
                PatchUtil.Failures.Add($"{name}: {e.Message}");
                api.Logger.Error("[Embalmed Eats] Patch group {0} failed: {1}", name, e);
            }
        }

        Run(TransitionPatches.Label, TransitionPatches.Apply);
        Run(NutritionPatches.Label, NutritionPatches.Apply);
        Run(EatingPatches.Label, EatingPatches.Apply);
        Run(CraftingPatches.Label, CraftingPatches.Apply);
        Run(NamePatches.Label, NamePatches.Apply);
        Run(BarrelPatches.Label, BarrelPatches.Apply);
        Run(DisplayPatches.Label, DisplayPatches.Apply);
        Run(GroundStoragePatches.Label, GroundStoragePatches.Apply);
        Run(HandbookPatches.Label, HandbookPatches.Apply);

        api.Logger.Notification("[Embalmed Eats] Applied {0} patches ({1} transition, {2} nutrition, {3} eating, {4} crafting, {5} barrel, {6} display). {7} problems.",
            PatchUtil.Patched.Count,
            PatchUtil.CountPatched(TransitionPatches.Label), PatchUtil.CountPatched(NutritionPatches.Label),
            PatchUtil.CountPatched(EatingPatches.Label), PatchUtil.CountPatched(CraftingPatches.Label),
            PatchUtil.CountPatched(BarrelPatches.Label), PatchUtil.CountPatched(DisplayPatches.Label),
            PatchUtil.Failures.Count);

        foreach (string f in PatchUtil.Failures) api.Logger.Warning("[Embalmed Eats] Patch problem: {0}", f);
    }

    void LoadConfig(ICoreAPI api)
    {
        EmbalmedEatsConfig? cfg = null;
        if (api.Side == EnumAppSide.Server)
        {
            ConfigLoadError = ConfigBackup = null;
        }
        try
        {
            cfg = api.LoadModConfig<EmbalmedEatsConfig>(ConfigFile);
        }
        catch (Exception e)
        {
            api.Logger.Error("[Embalmed Eats] Could not read ModConfig/{0}, using defaults: {1}", ConfigFile, e.Message);
            if (api.Side == EnumAppSide.Server)
            {
                // The defaults are written back below: keep the unreadable file first, so the changes in it aren't lost
                ConfigLoadError = e.Message;
                try
                {
                    ConfigBackup = DoctorFixes.Backup(api, ConfigFile);
                    if (ConfigBackup != null) api.Logger.Warning("[Embalmed Eats] The unreadable settings file was kept as {0}", ConfigBackup);
                }
                catch (Exception be) { api.Logger.Warning("[Embalmed Eats] Could not keep a copy of the unreadable settings file: {0}", be.Message); }
            }
        }

        cfg ??= new EmbalmedEatsConfig();
        if (cfg.ToxicityStages == null || cfg.ToxicityStages.Length == 0) cfg.ToxicityStages = new EmbalmedEatsConfig().ToxicityStages;

        if (api.Side == EnumAppSide.Server)
        {
            try { api.StoreModConfig(cfg, ConfigFile); } // writes defaults / new options back
            catch (Exception e) { api.Logger.Warning("[Embalmed Eats] Could not write config: {0}", e.Message); }
        }

        Preservation.Config = cfg;
        Preservation.ClearCache();
    }

    /// <summary>
    /// On the server: wax/resin recipes, ground storage and creative tabs. The item changes reach players with the
    /// item definitions (the same way vanilla adds behaviors); players' games repeat them only for items that
    /// didn't get them that way.
    /// </summary>
    public override void AssetsFinalize(ICoreAPI api)
    {
        Preservation.ClearCache();

        if (api is ICoreServerAPI sapi)
        {
            Step(api, "generate wax/resin recipes", () => GridRecipeGenerator.Generate(sapi));
        }

        Step(api, "add ground storage", () =>
        {
            int n = GroundStorageSupport.AddToPreservableItems(api);
            api.Logger.Notification("[Embalmed Eats] Ground storage added to {0} preservable items that didn't have it ({1}).", n, api.Side);
        });
        AddCreativeTabs(api);
    }

    /// <summary>
    /// On the server here, so the lists go to players with the item definitions. Players' games repeat it
    /// (BlockTexturesLoaded) for anything that didn't arrive that way; it skips food that already has them.
    /// </summary>
    void AddCreativeTabs(ICoreAPI api)
    {
        Step(api, "add creative tabs", () =>
        {
            int n = CreativeTabs.AddToCreativeInventory(api);
            CreativeAddedHere += n;
            if (n > 0) api.Logger.Notification("[Embalmed Eats] Creative tabs: {0} foods added ({1}).", n, api.Side);
        });
    }

    /// <summary>
    /// How many foods got their creative tabs on this side (for Vintage Doctor's comparison). On a player's game this
    /// should stay 0: the lists come with the server's items.
    /// </summary>
    public int CreativeAddedHere;

    static void Step(ICoreAPI api, string what, Action action)
    {
        try { action(); }
        catch (Exception e) { api.Logger.Error("[Embalmed Eats] Failed to {0}: {1}", what, e); }
    }

    public override void StartServerSide(ICoreServerAPI sapi)
    {
        this.sapi = sapi;
        toxicity = new ToxicitySystem(sapi);
        serverChannel = sapi.Network.GetChannel(ModId);
        sapi.Event.PlayerJoin += SendConfig;
        Commands.Register(sapi, this);
        // Vintage Doctor's checks, cards, page and fixes, over its event bus (no reference to the doctor needed)
        DoctorBridge.RegisterServer(sapi);
    }

    /// <summary>Sends the settings again to everyone playing (after a fix changed them).</summary>
    public void SendConfigToAll()
    {
        if (sapi == null) return;
        foreach (var player in sapi.World.AllOnlinePlayers)
        {
            if (player is IServerPlayer sp && sp.ConnectionState == EnumClientState.Playing) SendConfig(sp);
        }
    }

    public override void StartClientSide(ICoreClientAPI capi)
    {
        ConfigReceived = capi.IsSinglePlayer;
        capi.Network.GetChannel(ModId).SetMessageHandler<ConfigSyncPacket>(OnConfigReceived);
        DoctorBridge.RegisterClient(capi, Mod.Info.Version);
        // The documented point where a client has all blocks and items from the server
        capi.Event.BlockTexturesLoaded += () =>
        {
            Step(capi, "add ground storage", () => GroundStorageSupport.AddToPreservableItems(capi));
            AddCreativeTabs(capi);
        };
    }

    void SendConfig(IServerPlayer player)
    {
        try
        {
            serverChannel?.SendPacket(new ConfigSyncPacket { Json = JsonConvert.SerializeObject(Preservation.Config) }, player);
        }
        catch (Exception e)
        {
            sapi?.Logger.Warning("[Embalmed Eats] Could not send config to {0}: {1}", player.PlayerName, e.Message);
        }
    }

    void OnConfigReceived(ConfigSyncPacket packet)
    {
        // In singleplayer the server already set the shared config
        if (api is ICoreClientAPI capi && capi.IsSinglePlayer) return;
        try
        {
            var cfg = JsonConvert.DeserializeObject<EmbalmedEatsConfig>(packet.Json);
            if (cfg != null)
            {
                Preservation.Config = cfg;
                Preservation.ClearCache();
                ConfigReceived = true;
            }
        }
        catch (Exception e)
        {
            api.Logger.Warning("[Embalmed Eats] Bad config from server: {0}", e.Message);
        }
    }

    public override void Dispose()
    {
        if (sapi != null) sapi.Event.PlayerJoin -= SendConfig;
        toxicity?.Dispose();

        lock (patchLock)
        {
            patchUsers--;
            if (patchUsers <= 0 && harmony != null)
            {
                harmony.UnpatchAll(ModId);
                harmony = null;
                patchUsers = 0;
                PatchUtil.Reset();
            }
        }
    }
}
