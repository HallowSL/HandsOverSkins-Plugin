using System;
using System.Collections.Generic;
using HandsOverSkins.API;
using HandsOverSkins.Patches;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Jaws.Characters;
using UnityEngine;

namespace HandsOverSkins.Core;

/// <summary>
/// Cut-hand stumps for custom skins. The game cuts a hand by hiding the HandL/HandR part and switching on the
/// character's own "*_hand_L_cutted" (Gloria: "*_arm_L_cutted") mesh. Under a skin that replaces the hands, that
/// human stump would appear on the custom model, so it is kept hidden and its visibility is used only as the signal
/// that the hand is cut. The skin's own StumpL/StumpR meshes are shown instead; generated caps (material
/// "HO_AutoStump") wear the character's own stump material, so they match the game's gore.
/// </summary>
internal static class Stumps
{
    private const string AutoMaterial = "HO_AutoStump";
    private static readonly Dictionary<IntPtr, (CharacterSkinLogic logic, ESkinType skin)> _tracked = new();
    private static readonly Dictionary<IntPtr, Material> _gameStumpMat = new(); // per character root

    /// <summary>Called after the game applied <paramref name="skinType"/> (the TrySetSkin postfix knows it reliably).</summary>
    public static void Track(CharacterSkinLogic logic, ESkinType skinType)
    {
        if (logic == null) return;
        _tracked[logic.Pointer] = (logic, skinType);
        Update(logic);
    }

    public static void Untrack(CharacterSkinLogic logic)
    {
        if (logic != null) _tracked.Remove(logic.Pointer);
    }

    public static void UpdateAll()
    {
        List<IntPtr> dead = null;
        foreach (var kv in _tracked)
        {
            try
            {
                var root = kv.Value.logic._root;
                if (root == null || root.WasCollected) { (dead ??= new()).Add(kv.Key); continue; }
                Update(kv.Value.logic);
            }
            catch { (dead ??= new()).Add(kv.Key); }
        }
        if (dead != null) foreach (var k in dead) _tracked.Remove(k);
    }

    public static void Update(CharacterSkinLogic logic)
    {
        try { UpdateInternal(logic); }
        catch (Exception e) { Plugin.Logger.LogError($"Stumps: {e.Message}"); }
    }

    private static void UpdateInternal(CharacterSkinLogic logic)
    {
        if (logic == null) return;
        var root = MeshRoot(logic._root);
        if (root == null) return;

        CustomSkin skin = null;
        if (_tracked.TryGetValue(logic.Pointer, out var tr)) SkinAPI.TryGetSkin(tr.skin, out skin);
        bool customHands = skin != null && (skin.BodyParts.ContainsKey(ESkinBodyPart.HandL) || skin.BodyParts.ContainsKey(ESkinBodyPart.HandR));

        SkinnedMeshRenderer cutL = null, cutR = null;
        List<SkinnedMeshRenderer> stumpL = null, stumpR = null;
        foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (r == null) continue;
            var mesh = r.sharedMesh;
            if (skin != null && mesh != null && skin.StumpMeshes.TryGetValue(mesh.Pointer, out bool right))
            {
                if (right) (stumpR ??= new()).Add(r); else (stumpL ??= new()).Add(r);
                continue;
            }
            string n = r.name;
            if (!n.EndsWith("_cutted", StringComparison.OrdinalIgnoreCase)) continue;
            if (n.IndexOf("_L_", StringComparison.Ordinal) >= 0 || n.IndexOf("_l_", StringComparison.Ordinal) >= 0) cutL ??= r;
            else if (n.IndexOf("_R_", StringComparison.Ordinal) >= 0 || n.IndexOf("_r_", StringComparison.Ordinal) >= 0) cutR ??= r;
        }

        // the character's own stump material, for generated caps
        foreach (var c in new[] { cutL, cutR })
        {
            if (c == null) continue;
            var mats = HiddenParts.Original(c);
            if (mats.Length > 0 && mats[0] != null) { _gameStumpMat[root.Pointer] = mats[0]; break; }
        }

        Side(cutL, stumpL, customHands, root);
        Side(cutR, stumpR, customHands, root);
    }

    /// <summary>Which hands are currently cut (the game's own "_cutted" meshes are switched on).</summary>
    internal static (bool left, bool right) CutSides(CharacterSkinLogic logic)
    {
        bool l = false, r = false;
        var root = logic == null ? null : MeshRoot(logic._root);
        if (root == null) return (false, false);
        foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr == null || !smr.enabled || !smr.gameObject.activeInHierarchy) continue;
            string n = smr.name;
            if (!n.EndsWith("_cutted", StringComparison.OrdinalIgnoreCase)) continue;
            if (n.IndexOf("_L_", StringComparison.Ordinal) >= 0 || n.IndexOf("_l_", StringComparison.Ordinal) >= 0) l = true;
            else if (n.IndexOf("_R_", StringComparison.Ordinal) >= 0 || n.IndexOf("_r_", StringComparison.Ordinal) >= 0) r = true;
        }
        return (l, r);
    }

    /// <summary>The skin logic's root may be the rig's "root" bone; the meshes sit next to it, so climb until there are renderers.</summary>
    internal static Transform MeshRoot(Transform t)
    {
        Transform any = null;
        for (int i = 0; t != null && i < 3; i++, t = t.parent)
        {
            var rs = t.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (rs.Length == 0) continue;
            any ??= t;
            foreach (var r in rs) if (r != null && r.name.EndsWith("_cutted", StringComparison.OrdinalIgnoreCase)) return t;
        }
        return any;
    }

    private static void Side(SkinnedMeshRenderer gameStump, List<SkinnedMeshRenderer> ours, bool customHands, Transform root)
    {
        // The game switches its stump on with enabled / SetActive; that is the "hand is cut" signal.
        bool cut = gameStump != null && gameStump.enabled && gameStump.gameObject.activeInHierarchy;

        if (gameStump != null)
        {
            if (customHands) HiddenParts.HideRenderer(gameStump);
            else if (HiddenParts.IsHidden(gameStump)) HiddenParts.ShowRenderer(gameStump);
        }
        if (ours == null) return;

        foreach (var s in ours)
        {
            if (!cut) { HiddenParts.HideRenderer(s); continue; }
            var original = HiddenParts.Original(s);
            if (HiddenParts.IsHidden(s)) HiddenParts.ShowRenderer(s);
            if (original.Length > 0 && original[0] != null && original[0].name.IndexOf(AutoMaterial, StringComparison.Ordinal) >= 0
                && _gameStumpMat.TryGetValue(root.Pointer, out var gm) && gm != null)
            {
                var cur = s.sharedMaterials;
                if (cur.Length != 1 || cur[0] == null || cur[0].Pointer != gm.Pointer)
                {
                    var arr = new Il2CppReferenceArray<Material>(1);
                    arr[0] = gm;
                    s.sharedMaterials = arr;
                }
            }
            if (!s.enabled) s.enabled = true;
        }
    }
}
