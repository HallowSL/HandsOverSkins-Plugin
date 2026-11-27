using System;
using System.Collections.Generic;
using System.Linq;
using HandsOverSkins.Core;
using Jaws.Characters;
using Jaws.Providers;

namespace HandsOverSkins.API;

/// <summary>
/// Public entry point for other mods.
/// <code>
/// [BepInDependency(HandsOverSkins.Plugin.Guid)]
/// ...
/// SkinAPI.LoadSkinBundle(Path.Combine(dir, "myskins.hoskin"));
/// // or build a CustomSkin in code and call SkinAPI.RegisterSkin(skin);
/// </code>
/// Register skins from your plugin's Load(): they are injected when the game builds its
/// CharactersProvider (before any wardrobe/lobby/gameplay character is created).
/// </summary>
public static class SkinAPI
{
    private static readonly Dictionary<string, CustomSkin> _byId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, CustomSkin> _byType = new();

    /// <summary>First value used for custom skin numbers. Game skins use 0..5.</summary>
    public const int CustomSkinTypeBase = 1000;

    /// <summary>Fired after a skin passes validation and gets its numeric id.</summary>
    public static event Action<CustomSkin> SkinRegistered;
    /// <summary>Fired after all skins were injected into a CharactersProvider instance.</summary>
    public static event Action<CharactersProvider> SkinsInjected;

    public static IReadOnlyCollection<CustomSkin> Skins => _byId.Values;

    public static IEnumerable<CustomSkin> GetSkins(ECharacterType character) =>
        _byId.Values.Where(s => s.Character == character).OrderBy(s => s.Order).ThenBy(s => s.Id);

    public static bool TryGetSkin(string id, out CustomSkin skin) => _byId.TryGetValue(id ?? "", out skin);
    public static bool TryGetSkin(ESkinType type, out CustomSkin skin) => _byType.TryGetValue((int)type, out skin);
    public static bool IsCustom(ESkinType type) => _byType.ContainsKey((int)type);

    /// <summary>Loads a .hoskin AssetBundle immediately and registers every skin manifest inside it.</summary>
    public static IReadOnlyList<CustomSkin> LoadSkinBundle(string path) => SkinBundleLoader.LoadBundle(path);

    /// <summary>
    /// Deterministic skin number for an id. Every client computes the same value, so the
    /// number sent through LobbyClientData/GameplayClientData resolves to the same skin.
    /// </summary>
    public static int ComputeSkinType(string id)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in id.ToLowerInvariant()) { h ^= c; h *= 16777619; }
            return CustomSkinTypeBase + (int)(h & 0x3FFFFFFF);
        }
    }

    /// <summary>Validate and register a skin. Returns false (and logs why) when the skin is unusable.</summary>
    /// <summary>Built-in pseudo skins (the "Random" card): no meshes, so the regular checks don't apply.</summary>
    internal static bool RegisterBuiltin(CustomSkin skin)
    {
        int type = ComputeSkinType(skin.Id);
        if (_byId.ContainsKey(skin.Id) || _byType.ContainsKey(type)) return false;
        skin.SkinType = (ESkinType)type;
        _byId[skin.Id] = skin;
        _byType[type] = skin;
        SkinInjector.InjectLate(skin);
        return true;
    }

    public static bool RegisterSkin(CustomSkin skin)
    {
        if (skin == null) return false;
        if (string.IsNullOrWhiteSpace(skin.Id)) { Plugin.Logger.LogError("Skin without an id was rejected."); return false; }
        if (_byId.ContainsKey(skin.Id)) { Plugin.Logger.LogWarning($"Duplicate skin id '{skin.Id}' from {skin.Source} ignored."); return false; }
        if (skin.Character == ECharacterType.None) { Plugin.Logger.LogError($"Skin '{skin.Id}' has no character."); return false; }
        if (skin.Prefab == null && skin.BodyParts.Count == 0) { Plugin.Logger.LogError($"Skin '{skin.Id}' has no prefab/body parts."); return false; }
        if (!skin.BodyParts.Values.Any(l => l != null && l.Count > 0))
        { Plugin.Logger.LogError($"Skin '{skin.Id}' does not map any SkinnedMeshRenderer to a body part."); return false; }

        int type = ComputeSkinType(skin.Id);
        if (_byType.TryGetValue(type, out var clash))
        {
            Plugin.Logger.LogError($"Skin id hash collision: '{skin.Id}' and '{clash.Id}'. Rename one of them.");
            return false;
        }

        skin.SkinType = (ESkinType)type;
        _byId[skin.Id] = skin;
        _byType[type] = skin;
        Plugin.Logger.LogInfo($"Registered skin {skin.DisplayName ?? skin.Id} -> {skin}");

        SkinInjector.InjectLate(skin);
        try { SkinRegistered?.Invoke(skin); } catch (Exception e) { Plugin.Logger.LogError(e); }
        return true;
    }

    internal static void RaiseInjected(CharactersProvider p)
    {
        try { SkinsInjected?.Invoke(p); } catch (Exception e) { Plugin.Logger.LogError(e); }
    }
}
