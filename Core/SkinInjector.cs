using System;
using System.Collections.Generic;
using System.Linq;
using HandsOverSkins.API;
using Il2CppInterop.Runtime;
using Jaws.Characters;
using Jaws.Providers;
using Project.Tools.DictionaryHelp;
using UnityEngine;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace HandsOverSkins.Core;

/// <summary>
/// Pushes <see cref="CustomSkin"/>s into the game's <see cref="CharactersProvider"/>.
///
/// Game facts this relies on (from HANDS OVER's IL2CPP metadata, Jaws.Runtime.dll):
///  * CharactersProvider loads every CharacterConfig / CharacterSkinConfig from Resources "Configs/Characters"
///    and stores skins in  Dictionary&lt;ECharacterType, List&lt;CharacterSkinConfig&gt;&gt; _skins.
///  * CharacterSkinConfig = { characterType, skinType, order, requiredLevel, LocalizedString description,
///    Sprite wardrobeCardSprite, SerializableDictionary&lt;ESkinBodyPart, List&lt;SkinnedMeshRenderer&gt;&gt; bodyParts }.
///  * CharacterSkinLogic re-binds each skin renderer onto the character rig by bone name (MapBones).
///  * CharacterConfig.ghostMaterials / deathMaterials map material -> ghost/dead material (MaterialPair, matched by name).
/// </summary>
internal static class SkinInjector
{
    private static readonly HashSet<IntPtr> _injected = new();
    private static readonly List<CharactersProvider> _providers = new();
    private static bool _dumped;

    public static bool HasInjected => _providers.Count > 0;
    public static CharactersProvider LatestProvider
    {
        get
        {
            for (int i = _providers.Count - 1; i >= 0; i--)
                if (_providers[i] != null && !_providers[i].WasCollected) return _providers[i];
            return null;
        }
    }

    public static void EnsureInjected(CharactersProvider provider)
    {
        if (provider == null || provider.Pointer == IntPtr.Zero) return;
        if (_injected.Contains(provider.Pointer)) return;
        _injected.Add(provider.Pointer);

        try
        {
            SkinBundleLoader.LoadPending();
            RandomSkins.EnsureRegistered();
            foreach (var skin in SkinAPI.Skins) MaterialVariants.MarkBundleShaders(skin);
            ShaderResolver.Refresh();
            _providers.Add(provider);

            int ok = 0;
            foreach (var skin in SkinAPI.Skins.ToList())
                if (InjectInto(provider, skin)) ok++;

            Plugin.Logger.LogInfo($"Injected {ok}/{SkinAPI.Skins.Count} custom skin(s) into CharactersProvider.");

            if (!_dumped && Plugin.Cfg.DumpGameData.Value)
            {
                _dumped = true;
                GameDataDumper.Dump(provider);
            }
            SkinAPI.RaiseInjected(provider);
        }
        catch (Exception e) { Plugin.Logger.LogError($"Skin injection failed: {e}"); }
    }

    /// <summary>A skin registered after providers already exist (e.g. by another mod at runtime).</summary>
    public static void InjectLate(CustomSkin skin)
    {
        foreach (var p in _providers.ToList())
        {
            if (p == null || p.WasCollected) { _providers.Remove(p); continue; }
            InjectInto(p, skin);
        }
    }

    private static bool InjectInto(CharactersProvider provider, CustomSkin skin)
    {
        try
        {
            CharacterConfig character;
            if (!provider.TryGetCharacter(skin.Character, out character) || character == null)
            {
                Plugin.Logger.LogError($"{skin}: the game has no character '{skin.Character}'.");
                return false;
            }

            MaterialVariants.Prepare(skin, character);

            if (skin.Config == null || skin.Config.WasCollected)
                skin.Config = CreateConfig(provider, skin);

            var skinsByCharacter = provider._skins;
            Il2CppList.List<CharacterSkinConfig> list;
            if (!skinsByCharacter.TryGetValue(skin.Character, out list) || list == null)
            {
                list = new Il2CppList.List<CharacterSkinConfig>();
                skinsByCharacter.Add(skin.Character, list);
            }

            var managed = new List<CharacterSkinConfig>();
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c == null) continue;
                if (c.skinType == skin.SkinType)
                {
                    if (c.Pointer == skin.Config.Pointer) return true; // already there
                    Plugin.Logger.LogWarning($"{skin}: replacing an existing config with the same skin number.");
                    continue;
                }
                managed.Add(c);
            }
            managed.Add(skin.Config);
            managed.Sort((a, b) => a.order != b.order ? a.order.CompareTo(b.order) : ((int)a.skinType).CompareTo((int)b.skinType));

            list.Clear();
            foreach (var c in managed) list.Add(c);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"{skin}: injection failed: {e}");
            return false;
        }
    }

    private static CharacterSkinConfig CreateConfig(CharactersProvider provider, CustomSkin skin)
    {
        var cfg = ScriptableObject.CreateInstance(Il2CppType.Of<CharacterSkinConfig>()).Cast<CharacterSkinConfig>();
        cfg.name = $"{skin.Character} - {skin.DisplayName} - Custom Skin Config";
        cfg.hideFlags = HideFlags.DontUnloadUnusedAsset;

        cfg.characterType = skin.Character;
        cfg.skinType = skin.SkinType;
        cfg.order = skin.Order;
        cfg.requiredLevel = skin.RequiredLevel;
        cfg.description = LocalizationBridge.CreateDescription(skin);
        cfg.wardrobeCardSprite = skin.Portrait != null ? skin.Portrait : DefaultPortrait(provider, skin.Character);

        var parts = cfg.bodyParts ?? new SerializableDictionary<ESkinBodyPart, Il2CppList.List<SkinnedMeshRenderer>>();
        foreach (var kv in skin.BodyParts)
        {
            var l = new Il2CppList.List<SkinnedMeshRenderer>();
            foreach (var r in kv.Value) if (r != null) l.Add(r);
            if (parts.ContainsKey(kv.Key)) parts.Remove(kv.Key);
            parts.Add(kv.Key, l);
        }
        // The game hides a default part as soon as the skin declares it (Ben's "Baby" skin maps its hat to
        // both Hat and Hair). An empty list therefore hides the default part without adding geometry.
        foreach (var hidden in skin.HiddenParts)
            if (!parts.ContainsKey(hidden)) parts.Add(hidden, new Il2CppList.List<SkinnedMeshRenderer>());
        cfg.bodyParts = parts;
        return cfg;
    }

    private static Sprite DefaultPortrait(CharactersProvider provider, ECharacterType character)
    {
        try
        {
            CharacterSkinConfig def;
            if (provider.TryGetSkin(character, ESkinType.Default, out def) && def != null) return def.wardrobeCardSprite;
            CharacterConfig c;
            if (provider.TryGetCharacter(character, out c) && c != null) return c.wardrobeCardSprite;
        }
        catch { /* ignored */ }
        return null;
    }
}
