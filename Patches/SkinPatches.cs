using System;
using System.Collections.Generic;
using System.Linq;
using HandsOverSkins.API;
using HandsOverSkins.Core;
using HarmonyLib;
using Jaws.Characters;
using Jaws.MainMenu.UI.Wardrobe;
using Jaws.Providers;
using Jaws.Wardrobe;
using UnityEngine;
using UnityEngine.Localization;

namespace HandsOverSkins.Patches;

// ---------------------------------------------------------------------------------------------
// 1) Injection: as soon as the game builds its CharactersProvider (which loads Resources
//    "Configs/Characters"), add our CharacterSkinConfigs to it. GetSkins/TryGetSkin are guarded
//    too in case the constructor patch could not be applied.
// ---------------------------------------------------------------------------------------------
[HarmonyPatch(typeof(CharactersProvider), MethodType.Constructor)]
internal static class CharactersProvider_Ctor
{
    private static void Postfix(CharactersProvider __instance) => SkinInjector.EnsureInjected(__instance);
}

[HarmonyPatch(typeof(CharactersProvider), nameof(CharactersProvider.GetSkins))]
internal static class CharactersProvider_GetSkins
{
    private static void Prefix(CharactersProvider __instance) => SkinInjector.EnsureInjected(__instance);
}

[HarmonyPatch(typeof(CharactersProvider), nameof(CharactersProvider.TryGetSkin))]
internal static class CharactersProvider_TryGetSkin
{
    private static void Prefix(CharactersProvider __instance) => SkinInjector.EnsureInjected(__instance);
}

// ---------------------------------------------------------------------------------------------
// 2) Applying a skin (wardrobe preview, lobby, gameplay alive body and dead body all go through
//    CharacterSkinLogic.TrySetSkin). Skin numbers arrive over the network in
//    LobbyClientData/GameplayClientData, so a player without a given custom skin gets the
//    character's default instead of a broken model.
// ---------------------------------------------------------------------------------------------
[HarmonyPatch(typeof(CharacterSkinLogic), nameof(CharacterSkinLogic.TrySetSkin))]
internal static class CharacterSkinLogic_TrySetSkin
{
    private static void Prefix(CharacterSkinLogic __instance, ref ESkinType targetSkin, out ESkinType __state)
    {
        __state = targetSkin;
        try
        {
            HiddenParts.Restore(__instance);
            var provider = __instance._provider;
            if (provider == null) return;
            SkinInjector.EnsureInjected(provider);
            if (LiveSkins.TryOverride(__instance, out var live)) { targetSkin = live; __state = live; }
            if (RandomSkins.IsRandom(targetSkin))
            {
                targetSkin = RandomSkins.Resolve(targetSkin);
                __state = targetSkin;
            }

            if ((int)targetSkin < SkinAPI.CustomSkinTypeBase) return;

            CharacterSkinConfig cfg;
            if (!provider.TryGetSkin(__instance._characterType, targetSkin, out cfg) || cfg == null)
            {
                if (Plugin.Cfg.VerboseLogging.Value)
                    Plugin.Logger.LogInfo($"Skin #{(int)targetSkin} for {__instance._characterType} is not installed here, using Default.");
                targetSkin = ESkinType.Default;
            }
            __state = targetSkin;
        }
        catch (Exception e) { Plugin.Logger.LogError($"TrySetSkin prefix: {e}"); }
    }

    private static void Postfix(CharacterSkinLogic __instance, ESkinType __state)
    {
        try
        {
            ShaderResolver.RetryUnresolved();
            HiddenParts.Apply(__instance, __state);
            MaterialStates.Reapply(__instance);
            LiveSkins.Note(__instance, __state);
            Stumps.Track(__instance, __state);
            HeldItems.Apply(__instance, __state);
            if (SkinAPI.TryGetSkin(__state, out var skin))
            {
                SkinRendering.Match(__instance);    // same layer / rendering layers as the base skins (wardrobe camera)
                FollowRigs.Attach(__instance, skin); // no-op unless the skin keeps its own skeleton
            }
            else FollowRigs.Detach(__instance);
        }
        catch (Exception e) { Plugin.Logger.LogError($"TrySetSkin postfix: {e}"); }
    }
}

[HarmonyPatch(typeof(CharacterSkinLogic), nameof(CharacterSkinLogic.ShowHead))]
internal static class CharacterSkinLogic_ShowHead
{
    private static void Postfix(CharacterSkinLogic __instance)
    {
        HiddenParts.Reapply(__instance);
        Stumps.Update(__instance);
        FollowRigs.Refresh(__instance);
    }
}

[HarmonyPatch(typeof(CharacterSkinLogic), nameof(CharacterSkinLogic.ShowFullBody))]
internal static class CharacterSkinLogic_ShowFullBody
{
    private static void Postfix(CharacterSkinLogic __instance)
    {
        HiddenParts.Reapply(__instance);
        Stumps.Update(__instance);
        FollowRigs.Refresh(__instance);
    }
}

[HarmonyPatch(typeof(CharacterSkinLogic), nameof(CharacterSkinLogic.RestoreHands))]
internal static class CharacterSkinLogic_RestoreHands
{
    private static void Postfix(CharacterSkinLogic __instance)
    {
        HiddenParts.Reapply(__instance);
        Stumps.Update(__instance);
        FollowRigs.Refresh(__instance);
    }
}

[HarmonyPatch(typeof(CharacterSkinLogic), nameof(CharacterSkinLogic.CutHand))]
internal static class CharacterSkinLogic_CutHand
{
    private static void Postfix(CharacterSkinLogic __instance)
    {
        HiddenParts.Reapply(__instance);
        Stumps.Update(__instance);
        FollowRigs.Refresh(__instance);
    }
}

[HarmonyPatch(typeof(CharacterSkinLogic), nameof(CharacterSkinLogic.Dispose))]
internal static class CharacterSkinLogic_Dispose
{
    private static void Prefix(CharacterSkinLogic __instance)
    {
        HiddenParts.Forget(__instance);
        MaterialStates.Forget(__instance);
        LiveSkins.Forget(__instance);
        FollowRigs.Detach(__instance);
        Stumps.Untrack(__instance);
        HeldItems.Forget(__instance);
    }
}

// ---------------------------------------------------------------------------------------------
// 2b) Ghost / dead-body materials (see MaterialStates for why the game's own swap misses custom skins).
// ---------------------------------------------------------------------------------------------
[HarmonyPatch(typeof(Jaws.Gameplay.Character.Visual.CharacterVisualController), nameof(Jaws.Gameplay.Character.Visual.CharacterVisualController.ReplaceMaterials))]
internal static class CharacterVisual_ReplaceMaterials
{
    private static void Postfix(Jaws.Gameplay.Character.Visual.CharacterVisualController __instance, Jaws.Utilities.Materials.EMaterialContentType contentType)
    {
        try { MaterialStates.OnReplaceMaterials(__instance, contentType); }
        catch (Exception e) { Plugin.Logger.LogError($"Ghost materials: {e}"); }
    }
}

[HarmonyPatch(typeof(Jaws.Gameplay.Character.Visual.CharacterVisualController), nameof(Jaws.Gameplay.Character.Visual.CharacterVisualController.Initialize))]
internal static class CharacterVisual_Initialize
{
    // the corpse body always wears the dead materials; it's only shown once the player dies
    private static void Postfix(Jaws.Gameplay.Character.Visual.CharacterVisualController __instance)
    {
        try { MaterialStates.SetDead(__instance, false); MaterialStates.OnDeadBody(__instance); LiveSkins.Register(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"Dead materials (init): {e}"); }
    }
}

[HarmonyPatch(typeof(Jaws.Gameplay.Character.Visual.CharacterVisualController), nameof(Jaws.Gameplay.Character.Visual.CharacterVisualController.ActivateDeadBody))]
internal static class CharacterVisual_ActivateDeadBody
{
    // prefix too, so the dissolve controller picks up our materials if it reads them on activation
    private static void Prefix(Jaws.Gameplay.Character.Visual.CharacterVisualController __instance)
    {
        try { MaterialStates.OnDeadBody(__instance); } catch (Exception e) { Plugin.Logger.LogError($"Dead materials: {e}"); }
    }
    private static void Postfix(Jaws.Gameplay.Character.Visual.CharacterVisualController __instance)
    {
        try { MaterialStates.OnDeadBody(__instance); } catch (Exception e) { Plugin.Logger.LogError($"Dead materials: {e}"); }
    }
}

[HarmonyPatch(typeof(Jaws.Gameplay.Character.CharacterManager), nameof(Jaws.Gameplay.Character.CharacterManager.ClientHandleIsDeadChanged))]
internal static class CharacterManager_IsDeadChanged
{
    private static void Postfix(Jaws.Gameplay.Character.CharacterManager __instance, bool newValue)
    {
        try { MaterialStates.SetDead(__instance.characterVisualController, newValue); }
        catch (Exception e) { Plugin.Logger.LogError($"Ghost materials (dead state): {e}"); }
    }
}

// ---------------------------------------------------------------------------------------------
// 2c) Live (mid-round) skin changes: hide our sync lines from the chat and apply them.
// ---------------------------------------------------------------------------------------------
[HarmonyPatch(typeof(Jaws.Chat.ChatContainerView), nameof(Jaws.Chat.ChatContainerView.AddPlayerMessage))]
internal static class Chat_AddPlayerMessage
{
    private static bool Prefix(string messageText)
    {
        try { return !LiveSkins.TryHandleChat(messageText); }
        catch (Exception e) { Plugin.Logger.LogError($"Live skin chat: {e}"); return true; }
    }
}

// ---------------------------------------------------------------------------------------------
// 2d) Favourites: star on each skin card, starred skins pinned first.
// ---------------------------------------------------------------------------------------------
[HarmonyPatch(typeof(WardrobeCharactersView), nameof(WardrobeCharactersView.BuildSkinCards))]
internal static class WardrobeCharacters_BuildSkinCards
{
    private static void Postfix(WardrobeCharactersView __instance)
    {
        try { Favorites.Decorate(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"Favorites: {e}"); }
    }
}

[HarmonyPatch(typeof(WardrobeCharactersView), nameof(WardrobeCharactersView.ClearSkinCards))]
internal static class WardrobeCharacters_ClearSkinCards
{
    private static void Prefix() => Favorites.ForgetCards();
}

// ---------------------------------------------------------------------------------------------
// 3) Wardrobe unlocks (optional) and descriptions.
// ---------------------------------------------------------------------------------------------
[HarmonyPatch(typeof(WardrobeService), nameof(WardrobeService.IsUnlocked), typeof(CharacterSkinConfig))]
internal static class WardrobeService_IsUnlockedSkin
{
    private static void Postfix(WardrobeService __instance, CharacterSkinConfig skin, ref bool __result)
    {
        RandomSkins.Wardrobe = __instance;
        if (skin != null && RandomSkins.IsRandom(skin.skinType)) { __result = true; return; }
        if (__result || skin == null || !Plugin.Cfg.UnlockCustomSkins.Value) return;
        if (SkinAPI.IsCustom(skin.skinType)) __result = true;
    }
}

[HarmonyPatch(typeof(WardrobeView), nameof(WardrobeView.HandleDescriptionRequested))]
internal static class WardrobeView_HandleDescriptionRequested
{
    private static void Prefix(LocalizedString description, out CustomSkin __state)
    {
        __state = null;
        if (LocalizationBridge.TryGetSkin(description, out var skin))
        {
            __state = skin;
            LocalizationBridge.EnsureEntries();
        }
        else DescriptionGuard.Clear();
    }

    private static void Postfix(WardrobeView __instance, CustomSkin __state)
    {
        if (__state == null) return;
        try
        {
            var text = __instance.descriptionText;
            if (text != null) DescriptionGuard.Set(text, __state.FormatDescription(LocalizationBridge.CurrentLocaleCode()));
        }
        catch (Exception e) { Plugin.Logger.LogError($"Description postfix: {e}"); }
    }
}

[HarmonyPatch(typeof(WardrobeView), nameof(WardrobeView.Open))]
internal static class WardrobeView_Open
{
    private static void Prefix() => LocalizationBridge.EnsureEntries();
}

[HarmonyPatch(typeof(WardrobeView), nameof(WardrobeView.Close))]
internal static class WardrobeView_Close
{
    private static void Prefix() => DescriptionGuard.Clear();
}

// ---------------------------------------------------------------------------------------------
// Random skin: the player's chosen skin is read through PlayerDataService.SkinType when it is sent to the
// lobby/match. Substituting the real pick there means the real skin number goes over the network.
// ---------------------------------------------------------------------------------------------
[HarmonyPatch(typeof(Jaws.General.PlayerDataService), nameof(Jaws.General.PlayerDataService.SkinType), MethodType.Getter)]
internal static class PlayerDataService_GetSkinType
{
    private static void Postfix(ref ESkinType __result)
    {
        try { if (RandomSkins.IsRandom(__result)) __result = RandomSkins.Resolve(__result); }
        catch (Exception e) { Plugin.Logger.LogError($"Random skin: {e.Message}"); }
    }
}

[HarmonyPatch(typeof(WardrobeService), nameof(WardrobeService.TrySelectSkin))]
internal static class WardrobeService_TrySelectSkin
{
    // picking the Random card again rolls a new skin (and the preview shows it)
    private static void Prefix(WardrobeService __instance, CharacterSkinConfig skin)
    {
        RandomSkins.Wardrobe = __instance;
        if (skin != null && RandomSkins.IsRandom(skin.skinType)) RandomSkins.Reroll(skin.characterType);
    }
}

[HarmonyPatch(typeof(Jaws.Gameplay.UI.GameplayUiPresenter), nameof(Jaws.Gameplay.UI.GameplayUiPresenter.HandleToLobbyClicked))]
internal static class GameplayUi_ToLobby
{
    // leaving a match for the lobby: next read of the chosen skin rolls a new one
    private static void Postfix() { if (Plugin.Cfg.RandomRerollEachMatch.Value) RandomSkins.Reroll(); }
}

// ---------------------------------------------------------------------------------------------
// Hidden default parts: belt-and-braces on top of the empty body-part lists in the config.
// Renderer.forceRenderingOff is stripped from the game build, and toggling enabled/active would fight
// the game's own head/hand visibility logic, so a hidden renderer gets an empty material array
// (renders nothing, whatever its enabled state) and its materials are restored on the next skin change.
// ---------------------------------------------------------------------------------------------
internal static class HiddenParts
{
    private static readonly Dictionary<IntPtr, (ESkinType skin, List<SkinnedMeshRenderer> hidden)> _state = new();
    private static readonly Dictionary<IntPtr, Material[]> _savedMaterials = new();
    private static readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Material> NoMaterials = new(0);

    private static void Hide(SkinnedMeshRenderer r)
    {
        if (!_savedMaterials.ContainsKey(r.Pointer)) _savedMaterials[r.Pointer] = r.sharedMaterials.ToArray();
        r.sharedMaterials = NoMaterials;
    }

    private static void Show(SkinnedMeshRenderer r)
    {
        if (_savedMaterials.Remove(r.Pointer, out var mats)) r.sharedMaterials = mats;
    }

    // shared with Stumps so both agree on each renderer's original materials
    internal static void HideRenderer(SkinnedMeshRenderer r) { if (r != null && r.sharedMaterials.Length > 0) Hide(r); }
    internal static void ShowRenderer(SkinnedMeshRenderer r) { if (r != null) Show(r); }
    internal static Material[] Original(SkinnedMeshRenderer r) =>
        r == null ? Array.Empty<Material>() : _savedMaterials.TryGetValue(r.Pointer, out var m) ? m : r.sharedMaterials.ToArray();
    internal static bool IsHidden(SkinnedMeshRenderer r) => r != null && _savedMaterials.ContainsKey(r.Pointer);

    public static void Apply(CharacterSkinLogic logic, ESkinType skinType)
    {
        if (logic == null) return;
        if (!SkinAPI.TryGetSkin(skinType, out var skin))
        {
            _state.Remove(logic.Pointer);
            return;
        }
        // Parts to keep hidden: the ones the skin asks to hide, plus every default part the skin replaces.
        // The game only swaps parts when the skin is applied; later visibility calls (ShowHead/ShowFullBody with
        // defaults, death, resurrect) can switch the character's own head/hands back on underneath a custom skin.
        var parts = new HashSet<ESkinBodyPart>(skin.HiddenParts);
        foreach (var kv in skin.BodyParts)
            if (kv.Value != null && kv.Value.Count > 0 && Replaceable.Contains(kv.Key)) parts.Add(kv.Key);
        if (parts.Contains(ESkinBodyPart.Head)) parts.Add(ESkinBodyPart.Hair); // a replaced head never keeps the default hair
        if (parts.Count == 0)
        {
            _state.Remove(logic.Pointer);
            return;
        }
        var hidden = new List<SkinnedMeshRenderer>();
        var defaults = logic._defaultSkin;
        if (defaults != null)
        {
            foreach (var part in parts)
            {
                Il2CppSystem.Collections.Generic.List<SkinnedMeshRenderer> list;
                if (!defaults.TryGetValue(part, out list) || list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    var r = list[i];
                    if (r == null) continue;
                    Hide(r);
                    hidden.Add(r);
                }
            }
        }
        // Belt and braces: _defaultSkin doesn't always list every default renderer (seen on Garp: his outfit stayed
        // visible under a second full-body skin), so also catch the character's own meshes by their naming
        // (Garp_outfit, Ben_head, Lukas_head_hair, Gloria_arm_L, …).
        try
        {
            var root = Stumps.MeshRoot(logic._root);
            if (root != null)
            {
                var custom = CustomMeshes();
                foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (r == null || hidden.Contains(r)) continue;
                    var m = r.sharedMesh;
                    if (m != null && custom.Contains(m.Pointer)) continue;
                    var part = PartByName(r.name);
                    if (part == null || !parts.Contains(part.Value)) continue;
                    Hide(r);
                    hidden.Add(r);
                }
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"Hidden parts (by name): {e.Message}"); }
        _state[logic.Pointer] = (skinType, hidden);
    }

    private static HashSet<IntPtr> CustomMeshes()
    {
        var set = new HashSet<IntPtr>();
        foreach (var s in SkinAPI.Skins)
            foreach (var list in s.BodyParts.Values)
                foreach (var r in list)
                    if (r != null && r.sharedMesh != null) set.Add(r.sharedMesh.Pointer);
        return set;
    }

    /// <summary>Body part of one of the game's own character meshes, from its name; null for anything else.</summary>
    private static ESkinBodyPart? PartByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        string n = name.ToLowerInvariant();
        if (n.EndsWith("_cutted") || n.StartsWith("rope_")) return null;
        if (n.EndsWith("_outfit")) return ESkinBodyPart.Outfit;
        if (n.EndsWith("_hair")) return ESkinBodyPart.Hair;      // Ben_hair, Gloria_Hair, Lukas_head_hair
        if (n.EndsWith("_head")) return ESkinBodyPart.Head;
        if (n.EndsWith("_hand_l") || n.EndsWith("_arm_l")) return ESkinBodyPart.HandL;
        if (n.EndsWith("_hand_r") || n.EndsWith("_arm_r")) return ESkinBodyPart.HandR;
        return null;
    }

    private static readonly HashSet<ESkinBodyPart> Replaceable = new()
    { ESkinBodyPart.Hair, ESkinBodyPart.Head, ESkinBodyPart.Outfit, ESkinBodyPart.HandL, ESkinBodyPart.HandR };

    public static void Reapply(CharacterSkinLogic logic)
    {
        if (logic == null || !_state.TryGetValue(logic.Pointer, out var s)) return;
        foreach (var r in s.hidden)
        {
            if (r == null || r.WasCollected) continue;
            if (r.sharedMaterials.Length > 0) Hide(r);
        }
    }

    /// <summary>Safety net (4 Hz): re-hide default parts something switched back on (death/dead-body material swaps, resurrect, …).</summary>
    public static void ReapplyAll()
    {
        foreach (var kv in _state)
            foreach (var r in kv.Value.hidden)
            {
                if (r == null || r.WasCollected) continue;
                if (r.sharedMaterials.Length > 0) Hide(r);
            }
    }

    public static void Restore(CharacterSkinLogic logic)
    {
        if (logic == null || !_state.TryGetValue(logic.Pointer, out var s)) return;
        foreach (var r in s.hidden) if (r != null) Show(r);
        _state.Remove(logic.Pointer);
    }

    public static void Forget(CharacterSkinLogic logic)
    {
        if (logic == null || !_state.TryGetValue(logic.Pointer, out var s)) return;
        foreach (var r in s.hidden) if (r != null) _savedMaterials.Remove(r.Pointer);
        _state.Remove(logic.Pointer);
    }
}
