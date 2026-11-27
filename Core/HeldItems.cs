using System;
using System.Collections.Generic;
using HandsOverSkins.API;
using Jaws.Characters;
using UnityEngine;

namespace HandsOverSkins.Core;

/// <summary>
/// Per-skin offsets for things the character holds (the card holders Cards_left / Cards_right under the hand bones,
/// or any other named transform).
///
/// The holders' bind pose is nowhere near the hand (Cards_left sits ~0.8 m from hand.l); the game's animations move
/// them into the hand. So offsets are applied every frame on top of whatever pose the holder has this frame:
///   pose = base ∘ (position, Euler(rotation)), scale = rest.scale × scale
/// where base is the animated pose when the game animated the holder this frame, otherwise the fallback:
///   • game-hand offsets (older skins): the bind pose (same as the SDK preview of those skins);
///   • Follow Rig skins with followSkinHand: the hand bone itself with the bind rotation (what the SDK shows on the
///     model's A-pose), carried over to the model's hand: world = proxy ∘ (k · offset⁻¹ · c, offset⁻¹ · r).
/// </summary>
internal static class HeldItems
{
    private sealed class Item
    {
        public Transform T, G;                       // holder, its parent (game bone)
        public Vector3 RestP, RestS; public Quaternion RestR;
        public Vector3 OP; public Quaternion OR; public float S;
        public bool Follow;
        public string SkinId; public IntPtr Logic;
        public int Index = -1; public IntPtr Bone;   // proxy index in the rig that drove it last, and that rig's game bone
        public bool Written; public Vector3 WP; public Quaternion WR;  // local pose we left it in last frame
    }

    private static readonly List<Item> _items = new();
    private struct Rest { public Vector3 P, S; public Quaternion R; }
    private static readonly Dictionary<IntPtr, Rest> _rest = new();   // by transform: bind pose, first time seen

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
            bool followRig = skin.FollowRig != null && skin.FollowRig.Bones.Count > 0;

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
                    _rest[tr.Pointer] = rest = new Rest { P = tr.localPosition, R = tr.localRotation, S = tr.localScale };
                float s = Mathf.Max(0.01f, h.S <= 0f ? 1f : h.S);
                tr.localScale = rest.S * s;
                _items.Add(new Item
                {
                    T = tr, G = tr.parent, RestP = rest.P, RestR = rest.R, RestS = rest.S,
                    OP = V3(h.P), OR = Quaternion.Euler(V3(h.R)), S = s,
                    Follow = h.FollowSkinHand && followRig && tr.parent != null,
                    SkinId = skin.Id, Logic = logic.Pointer,
                });
            }
            if (Plugin.Cfg.VerboseLogging.Value) Plugin.Logger.LogInfo($"{skin}: {skin.HeldItems.Count} held item offset(s).");
        }
        catch (Exception e) { Plugin.Logger.LogError($"Held items: {e.Message}"); }
    }

    /// <summary>This frame's base pose for the holder (animated, or the fallback), with the skin's offset on top.</summary>
    private static void Compose(Item a, out Vector3 p, out Quaternion r)
    {
        var cp = a.T.localPosition; var cr = a.T.localRotation;
        bool animated = a.Written
            ? (cp - a.WP).sqrMagnitude > 1e-10f || Quaternion.Angle(cr, a.WR) > 0.01f
            : (cp - a.RestP).sqrMagnitude > 1e-10f || Quaternion.Angle(cr, a.RestR) > 0.01f;
        Vector3 bp; Quaternion br;
        if (animated) { bp = cp; br = cr; }
        else if (a.Follow) { bp = Vector3.zero; br = a.RestR; }
        else { bp = a.RestP; br = a.RestR; }
        p = bp + br * a.OP;
        r = br * a.OR;
    }

    /// <summary>Held items of skins without a follow rig (or not riding on the model's hand): every LateUpdate.</summary>
    public static void UpdateLocal()
    {
        for (int n = _items.Count - 1; n >= 0; n--)
        {
            var a = _items[n];
            if (a.T == null || a.T.WasCollected) { _items.RemoveAt(n); continue; }
            if (a.Follow) continue;
            Compose(a, out var p, out var r);
            a.T.localPosition = p; a.T.localRotation = r;
            a.Written = true; a.WP = a.T.localPosition; a.WR = a.T.localRotation;
        }
    }

    /// <summary>
    /// End of every follow-rig Drive: holders with followSkinHand ride on the model's hand. With proxy = game × offset
    /// they keep the pose relative to the proxy hand they'd have relative to the game hand.
    /// </summary>
    public static void Follow(string skinId, Transform[] game, Transform[] proxies, Quaternion[] offsets)
    {
        if (_items.Count == 0 || game == null) return;
        for (int n = 0; n < _items.Count; n++)
        {
            var a = _items[n];
            if (!a.Follow || a.SkinId != skinId || a.T == null || a.T.WasCollected || a.G == null) continue;
            int i = a.Index;
            if (i < 0 || i >= game.Length || game[i] == null || game[i].Pointer != a.Bone)
            {
                i = FindProxy(a.G, game);   // -1: this holder belongs to another character's rig
                if (i < 0) continue;
                a.Index = i; a.Bone = game[i].Pointer;
            }
            if (game[i] == null || proxies[i] == null) continue;
            Compose(a, out var c, out var q);
            var P = proxies[i];
            var inv = Quaternion.Inverse(offsets[i]);
            float ps = P.lossyScale.x, gs = game[i].lossyScale.x;
            float k = Mathf.Abs(ps) > 1e-6f ? gs / ps : 1f;
            // c is local to the holder's parent; if the proxy follows a bone further up, express it in that bone first
            if (game[i].Pointer != a.G.Pointer)
            {
                var gw = a.G.TransformPoint(c);
                q = Quaternion.Inverse(game[i].rotation) * (a.G.rotation * q);
                c = game[i].InverseTransformPoint(gw);
            }
            a.T.SetPositionAndRotation(P.TransformPoint(k * (inv * c)), P.rotation * inv * q);
            a.Written = true; a.WP = a.T.localPosition; a.WR = a.T.localRotation;
        }
    }

    private static int FindProxy(Transform g, Transform[] game)
    {
        for (var t = g; t != null; t = t.parent)
            for (int i = 0; i < game.Length; i++)
                if (game[i] != null && game[i].Pointer == t.Pointer) return i;
        return -1;
    }

    public static void Forget(CharacterSkinLogic logic) => RestoreFor(logic);

    private static void RestoreFor(CharacterSkinLogic logic)
    {
        if (logic == null) return;
        for (int n = _items.Count - 1; n >= 0; n--)
        {
            var a = _items[n];
            if (a.Logic != logic.Pointer && a.T != null && !a.T.WasCollected) continue;
            if (a.T != null && !a.T.WasCollected)
            {
                // back to the bind pose only if we were the last to move it (an animated holder is the animator's)
                if (!a.Written || ((a.T.localPosition - a.WP).sqrMagnitude < 1e-10f && Quaternion.Angle(a.T.localRotation, a.WR) < 0.01f))
                { a.T.localPosition = a.RestP; a.T.localRotation = a.RestR; }
                a.T.localScale = a.RestS;
            }
            _items.RemoveAt(n);
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
