using System;
using System.Collections.Generic;
using HandsOverSkins.API;
using Jaws.Characters;
using UnityEngine;

namespace HandsOverSkins.Core;

/// <summary>
/// Per-skin offsets for things the character holds (the card holders Cards_left / Cards_right under the hand bones,
/// or any other named transform). Same formula as the SDK preview (HOHeldItemPreview.Compose):
/// local = rest + rest.rotation × position, rest.rotation × Euler(rotation), rest.scale × scale.
/// The rest pose is remembered the first time a transform is touched and put back when another skin is worn.
/// </summary>
internal static class HeldItems
{
    private struct Rest { public Transform T; public Vector3 P; public Quaternion R; public Vector3 S; }
    private static readonly Dictionary<IntPtr, Rest> _rest = new();                 // by transform
    private static readonly Dictionary<IntPtr, List<IntPtr>> _byLogic = new();      // logic -> moved transforms

    // Follow Rig skins with followSkinHand: holders that ride on the model's hand bone (see Follow)
    private sealed class Attached
    {
        public Transform T, G;
        public Vector3 C; public Quaternion R;      // composed local pose under the game bone
        public string SkinId; public IntPtr Logic;
        public int Index = -1; public IntPtr Bone;  // proxy index in the rig that drove it last, and that rig's game bone
    }
    private static readonly List<Attached> _attached = new();

    public static void Apply(CharacterSkinLogic logic, ESkinType skinType)
    {
        if (logic == null) return;
        try
        {
            RestoreFor(logic);
            if (!SkinAPI.TryGetSkin(skinType, out var skin) || skin.HeldItems.Count == 0) return;
            var root = logic._root;
            if (root == null) return;
            // the card holders hang off the rig's hand bones: look under the skin logic's root, and only if a target
            // isn't there, one level up (never wider, so another character's holders can't be picked up)
            var byName = Index(root);
            Dictionary<string, Transform> upper = null;

            var moved = new List<IntPtr>();
            foreach (var h in skin.HeldItems)
            {
                Transform tr;
                if (!byName.TryGetValue(h.Target.Trim(), out tr) && root.parent != null)
                    (upper ??= Index(root.parent)).TryGetValue(h.Target.Trim(), out tr);
                if (tr == null)
                {
                    if (Plugin.Cfg.VerboseLogging.Value) Plugin.Logger.LogWarning($"{skin}: held item target '{h.Target}' not found.");
                    continue;
                }
                if (!_rest.TryGetValue(tr.Pointer, out var rest))
                    _rest[tr.Pointer] = rest = new Rest { T = tr, P = tr.localPosition, R = tr.localRotation, S = tr.localScale };
                var p = V3(h.P); var e = V3(h.R); float s = Mathf.Max(0.01f, h.S <= 0f ? 1f : h.S);
                tr.localPosition = rest.P + rest.R * p;
                tr.localRotation = rest.R * Quaternion.Euler(e);
                tr.localScale = rest.S * s;
                moved.Add(tr.Pointer);
                if (h.FollowSkinHand && skin.FollowRig != null && skin.FollowRig.Bones.Count > 0 && tr.parent != null)
                    _attached.Add(new Attached { T = tr, G = tr.parent, C = tr.localPosition, R = tr.localRotation, SkinId = skin.Id, Logic = logic.Pointer });
            }
            if (moved.Count > 0) _byLogic[logic.Pointer] = moved;
        }
        catch (Exception e) { Plugin.Logger.LogError($"Held items: {e.Message}"); }
    }

    public static void Forget(CharacterSkinLogic logic)
    {
        if (logic == null) return;
        _attached.RemoveAll(a => a.Logic == logic.Pointer);
        if (_byLogic.Remove(logic.Pointer, out var list))
            foreach (var p in list) _rest.Remove(p);
    }

    /// <summary>
    /// Called at the end of every follow-rig Drive: put attached holders on the model's hand. With
    /// proxy = game × offset, the holder keeps the same pose relative to the proxy hand as it has relative to the game
    /// hand: world = proxy ∘ (k · offset⁻¹ · C, offset⁻¹ · R), k = game scale / proxy scale.
    /// </summary>
    public static void Follow(string skinId, Transform[] game, Transform[] proxies, Quaternion[] offsets)
    {
        if (_attached.Count == 0 || game == null) return;
        for (int n = 0; n < _attached.Count; n++)
        {
            var a = _attached[n];
            if (a.SkinId != skinId || a.T == null || a.T.WasCollected || a.G == null) continue;
            int i = a.Index;
            if (i < 0 || i >= game.Length || game[i] == null || game[i].Pointer != a.Bone)
            {
                i = FindProxy(a.G, game);   // -1: this holder belongs to another character's rig
                if (i < 0) continue;
                a.Index = i; a.Bone = game[i].Pointer;
            }
            if (game[i] == null || proxies[i] == null) continue;
            var P = proxies[i];
            var inv = Quaternion.Inverse(offsets[i]);
            float ps = P.lossyScale.x, gs = game[i].lossyScale.x;
            float k = Mathf.Abs(ps) > 1e-6f ? gs / ps : 1f;
            // C is local to the holder's parent; if the proxy follows a bone further up, express C in that bone first
            Vector3 c = a.C; Quaternion r = a.R;
            if (game[i].Pointer != a.G.Pointer)
            {
                var gw = a.G.TransformPoint(c);
                c = game[i].InverseTransformPoint(gw);
                r = Quaternion.Inverse(game[i].rotation) * (a.G.rotation * r);
            }
            a.T.SetPositionAndRotation(P.TransformPoint(k * (inv * c)), P.rotation * inv * r);
        }
    }

    private static int FindProxy(Transform g, Transform[] game)
    {
        for (var t = g; t != null; t = t.parent)
            for (int i = 0; i < game.Length; i++)
                if (game[i] != null && game[i].Pointer == t.Pointer) return i;
        return -1;
    }

    private static void RestoreFor(CharacterSkinLogic logic)
    {
        _attached.RemoveAll(a => a.Logic == logic.Pointer || a.T == null || a.T.WasCollected);
        if (!_byLogic.Remove(logic.Pointer, out var list)) return;
        foreach (var p in list)
        {
            if (!_rest.TryGetValue(p, out var r) || r.T == null || r.T.WasCollected) continue;
            r.T.localPosition = r.P; r.T.localRotation = r.R; r.T.localScale = r.S;
        }
    }

    private static Dictionary<string, Transform> Index(Transform root)
    {
        var byName = new Dictionary<string, Transform>();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (t != null && !t.name.StartsWith("HOS_", StringComparison.Ordinal) && !byName.ContainsKey(t.name)) byName[t.name] = t;
        return byName;
    }

    private static Vector3 V3(float[] a) => a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.zero;
}
