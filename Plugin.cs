using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HandsOverSkins.Core;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;

namespace HandsOverSkins;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "com.handsover.customskins";
    public const string Name = "HandsOverSkins";
    public const string Version = "1.1.0";

    internal static ManualLogSource Logger;
    internal static PluginConfig Cfg;
    internal static Harmony HarmonyInstance;

    public override void Load()
    {
        Logger = Log;
        Cfg = new PluginConfig(Config);

        if (!ClassInjector.IsTypeRegisteredInIl2Cpp<SkinsRuntime>())
            ClassInjector.RegisterTypeInIl2Cpp<SkinsRuntime>();
        AddComponent<SkinsRuntime>();
        if (!ClassInjector.IsTypeRegisteredInIl2Cpp<FollowRigDriver>())
            ClassInjector.RegisterTypeInIl2Cpp<FollowRigDriver>();

        HarmonyInstance = new Harmony(Guid);
        HarmonyInstance.PatchAll(typeof(Plugin).Assembly);

        // Bundles are discovered now but their Unity objects are only touched once the
        // game constructs its CharactersProvider (all game shaders/materials are loaded by then).
        SkinBundleLoader.DiscoverBundles(Paths.PluginPath);
        if (!string.IsNullOrEmpty(Cfg.ExtraSkinFolder.Value) && Directory.Exists(Cfg.ExtraSkinFolder.Value))
            SkinBundleLoader.DiscoverBundles(Cfg.ExtraSkinFolder.Value);

        Logger.LogInfo($"{Name} {Version} loaded. {SkinBundleLoader.PendingCount} skin bundle(s) queued.");
    }
}

internal sealed class PluginConfig
{
    public readonly ConfigEntry<bool> UnlockCustomSkins;
    public readonly ConfigEntry<bool> RandomSkin;
    public readonly ConfigEntry<string> RandomPool;
    public readonly ConfigEntry<bool> RandomRerollEachMatch;
    public readonly ConfigEntry<bool> AutoGhostMaterials;
    public readonly ConfigEntry<bool> AutoDeadMaterials;
    public readonly ConfigEntry<string> DescriptionFormat;
    public readonly ConfigEntry<bool> DumpGameData;
    public readonly ConfigEntry<string> ExtraSkinFolder;
    public readonly ConfigEntry<bool> VerboseLogging;
    public readonly ConfigEntry<string> LocalizationTable;
    public readonly ConfigEntry<bool> HangingHandAnchor;
    public readonly ConfigEntry<float> HangingBlendStart;
    public readonly ConfigEntry<float> HangingBlendEnd;
    public readonly ConfigEntry<bool> ArmIK;
    public readonly ConfigEntry<bool> ArmIKAlways;
    public readonly ConfigEntry<bool> Jiggle;
    public readonly ConfigEntry<float> ArmIKForwardStart;
    public readonly ConfigEntry<float> ArmIKForwardEnd;
    public readonly ConfigEntry<bool> LiveChange;
    public readonly ConfigEntry<bool> LiveSync;
    public readonly ConfigEntry<string> LiveNextKey;
    public readonly ConfigEntry<string> LivePrevKey;
    public readonly ConfigEntry<string> LiveRandomKey;
    public readonly ConfigEntry<string> LiveFavoriteKey;
    public readonly ConfigEntry<bool> FavoritesEnabled;
    public readonly ConfigEntry<bool> FavoritesFirst;
    public readonly ConfigEntry<bool> RandomCardFirst;
    public readonly ConfigEntry<float> FavoriteStarSize;

    public PluginConfig(ConfigFile cfg)
    {
        UnlockCustomSkins = cfg.Bind("Skins", "UnlockCustomSkins", false,
            "Ignore the RequiredLevel of custom skins and treat them as always unlocked.");
        RandomSkin = cfg.Bind("Random Skin", "Enabled", true,
            "Add a 'Random' card to every character's wardrobe. With it selected you get one of that character's skins at random.");
        RandomPool = cfg.Bind("Random Skin", "Pool", "Unlocked",
            new ConfigDescription("Which skins Random picks from. Favorites = only the skins you starred in the wardrobe (falls back to Unlocked while none are starred).", new AcceptableValueList<string>("Unlocked", "CustomOnly", "Favorites", "All")));
        RandomRerollEachMatch = cfg.Bind("Random Skin", "RerollEachMatch", true,
            "Pick a new random skin after every match (otherwise it changes only when you select Random again or restart).");
        AutoGhostMaterials = cfg.Bind("Materials", "AutoGhostMaterials", true,
            "Generate a ghost material (game shader 'MS/Ghost Unlit') for custom skin materials that don't ship one.");
        AutoDeadMaterials = cfg.Bind("Materials", "AutoDeadMaterials", true,
            "Generate a dead-body material (game shader 'Dissolve/SH_Dissolve_Lit5') for custom skin materials that don't ship one.");
        DescriptionFormat = cfg.Bind("UI", "DescriptionFormat",
            "<size=120%><b>{name}</b></size>\n{description}\n<size=75%><alpha=#99>by {author}</size>",
            "How custom skin descriptions are rendered in the wardrobe. Tokens: {name} {description} {author} {version} {character}.");
        LocalizationTable = cfg.Bind("UI", "LocalizationTable", "General",
            "String table collection the game uses for wardrobe descriptions. Only change this if a game update renames it.");
        DumpGameData = cfg.Bind("Developer", "DumpGameData", false,
            "Write the game's skins, shaders (with properties) and rig bone names to BepInEx/HandsOverSkins_Dump on load. Use it to refresh the SDK after game updates.");
        ExtraSkinFolder = cfg.Bind("Developer", "ExtraSkinFolder", "",
            "Optional extra folder scanned for *.hoskin bundles (handy while authoring: point it at the SDK's build output).");
        VerboseLogging = cfg.Bind("Developer", "VerboseLogging", false, "Log every patch decision.");
        HangingHandAnchor = cfg.Bind("Follow Rig", "HangingHandAnchor", true,
            "When both of the character's hands are above its head (hanging from the lobby rope), move follow-rig skins so their hands meet the tied hands. " +
            "Keeps smaller or differently proportioned models attached to the rope.");
        HangingBlendStart = cfg.Bind("Follow Rig", "HangingBlendStart", 0.02f, "Hands this far (m) above the head start blending toward the hand anchor.");
        HangingBlendEnd = cfg.Bind("Follow Rig", "HangingBlendEnd", 0.15f, "Hands this far (m) above the head are fully hand-anchored.");
        ArmIK = cfg.Bind("Follow Rig", "ArmIK", true,
            "Bend follow-rig arms (two-bone IK) so their hands reach the character's real hand positions when the hands are interacting: " +
            "in front of the chest (table, cards, crank) or above the head (rope). Relaxed arms are left alone.");
        ArmIKAlways = cfg.Bind("Follow Rig", "ArmIKAlways", true,
            "Always put the hands on the character's real hand positions (so held props like cash/cards/crank line up). " +
            "Set false to only use IK when the hands are in front of the chest or raised (better for very small models standing with relaxed arms).");
        Jiggle = cfg.Bind("Follow Rig", "Jiggle", true,
            "Run the spring ('jiggle') bones that Follow Rig skins define (hair, tails, ears, …).");
        ArmIKForwardStart = cfg.Bind("Follow Rig", "ArmIKForwardStart", 0.10f, "Hand this far (m) in front of the chest starts blending in the arm IK.");
        ArmIKForwardEnd = cfg.Bind("Follow Rig", "ArmIKForwardEnd", 0.25f, "Hand this far (m) in front of the chest gets full arm IK.");
        LiveChange = cfg.Bind("Live Change", "Enabled", true,
            "Change your skin in the middle of a match with the hotkeys below (not while typing in chat).");
        LiveSync = cfg.Bind("Live Change", "SyncToOthers", true,
            "Tell the other players about the change through the game chat. Players with HandsOverSkins apply it and hide the line; " +
            "players without the mod see a short 'HOSKIN …' line in chat.");
        LiveNextKey = cfg.Bind("Live Change", "NextSkinKey", "F7", "Next skin (Unity Input System key name, e.g. F7, PageDown, Numpad6). Empty = off.");
        LivePrevKey = cfg.Bind("Live Change", "PreviousSkinKey", "F6", "Previous skin.");
        LiveRandomKey = cfg.Bind("Live Change", "RandomSkinKey", "F8", "Random unlocked skin.");
        LiveFavoriteKey = cfg.Bind("Live Change", "RandomFavoriteKey", "F9", "Random skin from your favourites.");
        FavoritesEnabled = cfg.Bind("Favorites", "Enabled", true, "Show a star on every wardrobe skin card to mark favourites.");
        FavoritesFirst = cfg.Bind("Favorites", "PinToTop", true, "List favourite skins first on each character's skin page.");
        RandomCardFirst = cfg.Bind("Favorites", "RandomCardFirst", true, "Put the Random card at the very front (before the favourites).");
        FavoriteStarSize = cfg.Bind("Favorites", "StarSize", 44f, "Size of the star button in UI units.");
    }
}
