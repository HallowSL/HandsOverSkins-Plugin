using System;
using System.Collections.Generic;
using HandsOverSkins.API;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Jaws.Characters;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HandsOverSkins.Core;

/// <summary>
/// Runtime side of "Follow Rig" skins (Humanoid models that keep their own proportions, e.g. creatures).
///
/// The SDK exports the model's own skeleton (posed into the game rig's rest pose) as a proxy rig, and
/// binds the meshes to it. In game we build that proxy skeleton under the character's live rig "root",
/// point the skin renderers at it, and every LateUpdate set each proxy bone's world rotation =
/// game bone rotation × rest offset (hips proxy also follows the game hips position).
///
/// Two independent ways keep renderers bound to the proxies:
///   • right after CharacterSkinLogic.TrySetSkin (Harmony postfix → <see cref="Attach"/>), and
///   • <see cref="Scan"/>, a 4 Hz safety net that finds live renderers using a follow-rig mesh,
///     locates their character rig by walking up the hierarchy and re-binds them if needed.
///     It doesn't depend on any game method, so it also covers code paths the patch misses.
/// </summary>
internal static class FollowRigs
{
    private sealed class State
    {
        public CustomSkin Skin;
        public Transform GameRoot;
        public GameObject Root;
        public Transform[] Proxies;
        public Transform[] Game;
        public Quaternion[] Offsets;
        public int Hips = -1;
        // Top-level proxy bones (parent = game "root") are anchored to the game hips with their rest offset,
        // so rigs whose arms/legs hang off a root bone (not the spine) move with the body.
        public int[] TopLevel = Array.Empty<int>();
        public Vector3[] AnchorPos;
        public Quaternion[] AnchorRot;
        // hanging hand anchor
        public int HandL = -1, HandR = -1;
        public int UpperL = -1, UpperR = -1, LowerL = -1, LowerR = -1;
        public Transform GameHead, GameChest, GameThighL, GameThighR;
        public JiggleSim Jiggle;
        public bool Dead => Root == null || Root.WasCollected || GameRoot == null || GameRoot.WasCollected;
    }

    private sealed class CharRig
    {
        public Transform CharRoot;
        public Transform GameRoot;
        public Dictionary<string, Transform> Bones;
        public uint Mask;
        public int Layer = -1;
        public bool Dead => GameRoot == null || GameRoot.WasCollected;
    }

    // states keyed by (game rig root, skin id)
    private static readonly Dictionary<(IntPtr, string), State> _states = new();
    private static readonly Dictionary<IntPtr, State> _byDriver = new();
    private static readonly Dictionary<IntPtr, CharRig> _chars = new();

    // index of custom-skin meshes / asset renderers
    private static readonly Dictionary<IntPtr, CustomSkin> _followMeshes = new();
    // name fallback: "<mesh name>|<vertex count>" → (skin, slots). Covers mesh instances that aren't the bundle object.
    private static readonly Dictionary<string, (CustomSkin skin, int[] slots)> _followByName = new();
    private static readonly HashSet<string> _customMeshNames = new();
    private static readonly HashSet<IntPtr> _reported = new();
    private static readonly HashSet<IntPtr> _warned = new();
    private static readonly Dictionary<IntPtr, int> _rebinds = new();
    private static readonly HashSet<IntPtr> _customMeshes = new();
    private static readonly HashSet<IntPtr> _assetRenderers = new();
    private static readonly HashSet<IntPtr> _styled = new();
    private static int _indexedSkins = -1;
    private static float _nextScan;

    // ------------------------------------------------------------------------------- patch path
    public static void Attach(CharacterSkinLogic logic, CustomSkin skin)
    {
        if (logic == null || skin == null) return;
        var rig = logic._rigBones;
        if (rig == null) return;
        Transform gameRoot;
        if (!rig.TryGetValue("root", out gameRoot) || gameRoot == null)
        {
            Transform hips;
            gameRoot = rig.TryGetValue("root.x", out hips) && hips != null ? hips.parent : logic._root;
        }
        if (gameRoot == null) return;

        BuildIndex();
        // Switching skins on this character: drop proxy rigs of other skins.
        DropOtherSkins(gameRoot, skin);
        if (skin.FollowRig == null || skin.FollowRig.Bones.Count == 0) return;

        // _rigBones only holds bones the character's own meshes use; face bones such as eyelid_top.l may be missing
        // (Lucas's head is skinned to a different face rig), so fall back to the full hierarchy under the game root.
        Dictionary<string, Transform> all = null;
        var st = GetState(gameRoot, skin, name =>
        {
            Transform t;
            if (rig.TryGetValue(name, out t) && t != null) return t;
            if (all == null)
            {
                all = new Dictionary<string, Transform>();
                foreach (var b in gameRoot.GetComponentsInChildren<Transform>(true))
                    if (b != null && !b.name.StartsWith("HOS_") && !all.ContainsKey(b.name)) all[b.name] = b;
            }
            return all.TryGetValue(name, out t) ? t : null;
        });
        if (st == null) return;

        var current = logic.CurrentSkin;
        if (current == null) return;
        foreach (var kv in current)
        {
            var list = kv.Value;
            if (list == null) continue;
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                if (r == null) continue;
                if (TryBind(r, st)) Plugin.Logger.LogInfo($"Follow rig: bound '{r.name}' via TrySetSkin.");
                else if ((r.sharedMesh == null || Lookup(r.sharedMesh).skin == null) && _warned.Add(r.Pointer))
                    Plugin.Logger.LogWarning($"Follow rig: CurrentSkin renderer '{r.name}' mesh '{r.sharedMesh?.name}' isn't a known follow-rig mesh.");
            }
        }
        Drive(st);
    }

    public static void Refresh(CharacterSkinLogic logic) => _nextScan = 0f; // let the scanner re-check right away

    public static void Detach(CharacterSkinLogic logic)
    {
        if (logic == null) return;
        var rig = logic._rigBones;
        Transform gameRoot = null;
        if (rig != null) rig.TryGetValue("root", out gameRoot);
        if (gameRoot != null) DropOtherSkins(gameRoot, null);
    }

    // ------------------------------------------------------------------------------ scanner path
    /// <summary>Called every frame from <see cref="SkinsRuntime"/>; does real work 4× per second.</summary>
    public static void Scan()
    {
        if (Time.realtimeSinceStartup < _nextScan) return;
        _nextScan = Time.realtimeSinceStartup + 0.25f;
        if (!SkinInjector.HasInjected) return;

        BuildIndex();
        if (_customMeshes.Count == 0) return;
        Cleanup();

        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<SkinnedMeshRenderer>()))
        {
            var r = o.TryCast<SkinnedMeshRenderer>();
            if (r == null || _assetRenderers.Contains(r.Pointer)) continue;
            var mesh = r.sharedMesh;
            if (mesh == null) continue;
            if (!_customMeshes.Contains(mesh.Pointer) && !_customMeshNames.Contains(Clean(mesh.name))) continue;
            var go = r.gameObject;
            if (go == null || !go.activeInHierarchy) continue;

            var cr = FindCharRig(r.transform);
            if (cr == null) continue;

            if (_styled.Add(r.Pointer)) Style(r, cr);

            var hit = Lookup(mesh);
            Report(r, mesh, cr, hit.skin != null);
            if (hit.skin != null)
            {
                var st = GetState(cr.GameRoot, hit.skin, name => cr.Bones.TryGetValue(name, out var t) ? t : null);
                if (st != null && TryBind(r, st))
                {
                    int n = _rebinds.TryGetValue(r.Pointer, out var c) ? c + 1 : 1;
                    _rebinds[r.Pointer] = n;
                    if (n == 1) Plugin.Logger.LogInfo($"Follow rig: bound '{r.name}' to {hit.skin.Id} proxies under '{cr.CharRoot.name}'.");
                    else if (n == 3 || n % 50 == 0)
                        Plugin.Logger.LogWarning($"Follow rig: '{r.name}' had its bones reset {n - 1}× by something else (re-bound each time).");
                }
            }
        }
    }

    private static void BuildIndex()
    {
        if (_indexedSkins == SkinAPI.Skins.Count) return;
        _indexedSkins = SkinAPI.Skins.Count;
        _followMeshes.Clear(); _customMeshes.Clear(); _assetRenderers.Clear(); _followByName.Clear(); _customMeshNames.Clear();
        foreach (var s in SkinAPI.Skins)
        {
            foreach (var list in s.BodyParts.Values)
                foreach (var r in list)
                {
                    if (r == null) continue;
                    _assetRenderers.Add(r.Pointer);
                    var m = r.sharedMesh;
                    if (m == null) continue;
                    _customMeshes.Add(m.Pointer);
                    _customMeshNames.Add(Clean(m.name));
                    if (s.FollowSlots.TryGetValue(m.Pointer, out var slots))
                    {
                        _followMeshes[m.Pointer] = s;
                        _followByName[Clean(m.name) + "|" + m.vertexCount] = (s, slots);
                    }
                }
        }
    }

    /// <summary>Walk up from a renderer to the character transform that owns a "root/root.x" game rig.</summary>
    private static CharRig FindCharRig(Transform t)
    {
        for (var a = t.parent; a != null; a = a.parent)
        {
            if (_chars.TryGetValue(a.Pointer, out var cached))
            {
                if (!cached.Dead) return cached;
                _chars.Remove(a.Pointer);
            }
            var gameRoot = a.Find("root");
            if (gameRoot == null || gameRoot.Find("root.x") == null) continue;

            var cr = new CharRig { CharRoot = a, GameRoot = gameRoot, Bones = new Dictionary<string, Transform>() };
            foreach (var b in gameRoot.GetComponentsInChildren<Transform>(true))
                if (b != null && !cr.Bones.ContainsKey(b.name) && !b.name.StartsWith("HOS_")) cr.Bones[b.name] = b;
            cr.Bones["root"] = gameRoot;

            // reference look: any non-custom skinned renderer of this character
            foreach (var smr in a.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var m = smr.sharedMesh;
                if (m == null || _customMeshes.Contains(m.Pointer)) continue;
                cr.Layer = smr.gameObject.layer;
                cr.Mask = smr.renderingLayerMask;
                break;
            }
            _chars[a.Pointer] = cr;
            return cr;
        }
        return null;
    }

    private static void Style(SkinnedMeshRenderer r, CharRig cr)
    {
        if (cr.Layer >= 0)
        {
            r.gameObject.layer = cr.Layer;
            r.renderingLayerMask = cr.Mask;
        }
        r.updateWhenOffscreen = true;
    }

    // ------------------------------------------------------------------------------- core
    private static State GetState(Transform gameRoot, CustomSkin skin, Func<string, Transform> findBone)
    {
        var key = (gameRoot.Pointer, skin.Id);
        if (_states.TryGetValue(key, out var st))
        {
            if (!st.Dead) return st;
            Destroy(st);
            _states.Remove(key);
        }
        st = Build(gameRoot, skin, findBone);
        if (st != null) _states[key] = st;
        return st;
    }

    /// <summary>Point a renderer at the proxy bones. Returns true when something changed.</summary>
    private static bool TryBind(SkinnedMeshRenderer smr, State st)
    {
        var mesh = smr != null ? smr.sharedMesh : null;
        if (mesh == null) return false;
        var hit = Lookup(mesh);
        var slots = hit.skin == st.Skin ? hit.slots : null;
        if (slots == null || slots.Length == 0) return false;


        var current = smr.bones;
        if (current != null && current.Length == slots.Length && current[0] != null && current[0].Pointer == st.Proxies[slots[0]].Pointer)
            return false;

        var bones = new Il2CppReferenceArray<Transform>(slots.Length);
        for (int b = 0; b < slots.Length; b++) bones[b] = st.Proxies[slots[b]];
        smr.bones = bones;
        smr.rootBone = st.Proxies[st.Hips >= 0 ? st.Hips : 0];
        smr.updateWhenOffscreen = true;
        return true;
    }

    private static State Build(Transform gameRoot, CustomSkin skin, Func<string, Transform> findBone)
    {
        var data = skin.FollowRig.Bones;
        var st = new State
        {
            Skin = skin,
            GameRoot = gameRoot,
            Root = new GameObject("HOS_FollowRig_" + skin.Id),
            Proxies = new Transform[data.Count],
            Game = new Transform[data.Count],
            Offsets = new Quaternion[data.Count],
        };
        st.Root.transform.SetParent(gameRoot, false);

        int following = 0;
        for (int i = 0; i < data.Count; i++)
        {
            var b = data[i];
            var t = new GameObject("HOS_" + (b.Name ?? ("bone" + i))).transform;
            t.SetParent(b.Parent >= 0 && b.Parent < i ? st.Proxies[b.Parent] : st.Root.transform, false);
            t.localPosition = V3(b.P);
            t.localRotation = Q(b.R);
            st.Proxies[i] = t;

            if (string.IsNullOrEmpty(b.GameBone)) continue;
            var g = findBone(b.GameBone);
            if (g == null) continue;
            st.Game[i] = g;
            st.Offsets[i] = Q(b.Offset);
            following++;
            if (b.GameBone == "root.x") st.Hips = i;
            else if (b.GameBone == "hand.l") st.HandL = i;
            else if (b.GameBone == "hand.r") st.HandR = i;
            else if (b.GameBone == "arm_stretch.l") st.UpperL = i;
            else if (b.GameBone == "arm_stretch.r") st.UpperR = i;
            else if (b.GameBone == "forearm_stretch.l") st.LowerL = i;
            else if (b.GameBone == "forearm_stretch.r") st.LowerR = i;
        }
        st.GameHead = findBone("head.x");
        st.GameChest = findBone("spine_03.x") ?? findBone("spine_02.x");
        st.GameThighL = findBone("thigh_stretch.l");
        st.GameThighR = findBone("thigh_stretch.r");

        ComputeAnchors(st, data);
        BuildJiggle(st);

        var driver = st.Root.AddComponent<FollowRigDriver>();
        _byDriver[driver.Pointer] = st;
        Plugin.Logger.LogInfo($"{skin}: follow rig built under '{gameRoot.parent?.name}' ({data.Count} bones, {following} following game bones{(st.Hips < 0 ? ", NO HIPS" : "")}).");
        Drive(st);
        return st;
    }

    private static void BuildJiggle(State st)
    {
        var groups = st.Skin.FollowRig.Jiggle;
        if (groups == null || groups.Count == 0) return;
        var items = new List<(int idx, Vector3 tip, FollowJiggle g)>();
        foreach (var g in groups)
        {
            if (g?.Bones == null) continue;
            for (int k = 0; k < g.Bones.Count; k++)
            {
                int i = g.Bones[k];
                if (i < 0 || i >= st.Proxies.Length || st.Game[i] != null || g.Tips == null || g.Tips.Count < k * 3 + 3) continue;
                items.Add((i, new Vector3(g.Tips[k * 3], g.Tips[k * 3 + 1], g.Tips[k * 3 + 2]), g));
            }
        }
        if (items.Count == 0) return;
        items.Sort((a, b) => a.idx.CompareTo(b.idx)); // proxies are parent-first
        var sim = new JiggleSim();
        var data = st.Skin.FollowRig.Bones;
        foreach (var it in items)
            sim.Add(st.Proxies[it.idx], Q(data[it.idx].R), it.tip, it.g.Stiffness, it.g.Damping, it.g.Gravity);
        st.Jiggle = sim;
        Plugin.Logger.LogInfo($"{st.Skin}: {sim.Count} jiggle bone(s).");
    }

    /// <summary>Rest pose of every proxy (in game-root space) → anchor offsets of top-level bones relative to the game hips.</summary>
    private static void ComputeAnchors(State st, List<FollowBone> data)
    {
        if (st.Hips < 0) return;
        int n = data.Count;
        var restPos = new Vector3[n];
        var restRot = new Quaternion[n];
        var top = new List<int>();
        for (int i = 0; i < n; i++)
        {
            var b = data[i];
            var lp = V3(b.P); var lr = Q(b.R);
            if (b.Parent >= 0 && b.Parent < i)
            {
                restPos[i] = restPos[b.Parent] + restRot[b.Parent] * lp;
                restRot[i] = restRot[b.Parent] * lr;
            }
            else { restPos[i] = lp; restRot[i] = lr; top.Add(i); }
        }
        // game hips rest = proxy hips rest position (aligned by the SDK) with rotation = proxy rotation × offset⁻¹
        var hipsPos = restPos[st.Hips];
        var hipsRotInv = Quaternion.Inverse(restRot[st.Hips] * Quaternion.Inverse(st.Offsets[st.Hips]));
        st.TopLevel = top.ToArray();
        st.AnchorPos = new Vector3[top.Count];
        st.AnchorRot = new Quaternion[top.Count];
        for (int k = 0; k < top.Count; k++)
        {
            int i = top[k];
            st.AnchorPos[k] = hipsRotInv * (restPos[i] - hipsPos);
            st.AnchorRot[k] = hipsRotInv * restRot[i];
        }
    }

    private static void DropOtherSkins(Transform gameRoot, CustomSkin keep)
    {
        var drop = new List<(IntPtr, string)>();
        foreach (var kv in _states)
            if (kv.Key.Item1 == gameRoot.Pointer && (keep == null || kv.Value.Skin != keep)) drop.Add(kv.Key);
        foreach (var k in drop) { Destroy(_states[k]); _states.Remove(k); }
    }

    private static void Cleanup()
    {
        var dead = new List<(IntPtr, string)>();
        foreach (var kv in _states) if (kv.Value.Dead) dead.Add(kv.Key);
        foreach (var k in dead) { Destroy(_states[k]); _states.Remove(k); }
        var deadChars = new List<IntPtr>();
        foreach (var kv in _chars) if (kv.Value.Dead) deadChars.Add(kv.Key);
        foreach (var k in deadChars) _chars.Remove(k);
        if (_styled.Count > 4096) _styled.Clear();
    }

    private static void Destroy(State st)
    {
        foreach (var k in new List<IntPtr>(_byDriver.Keys)) if (_byDriver[k] == st) _byDriver.Remove(k);
        if (st.Root != null && !st.Root.WasCollected) Object.Destroy(st.Root);
    }

    /// <summary>Called by <see cref="FollowRigDriver"/> every LateUpdate.</summary>
    public static void Drive(IntPtr driver)
    {
        if (_byDriver.TryGetValue(driver, out var st)) Drive(st);
    }

    private static void Drive(State st)
    {
        var p = st.Proxies;
        if (st.Hips >= 0 && st.Game[st.Hips] != null)
        {
            var gh = st.Game[st.Hips];
            Vector3 hp = gh.position; Quaternion hr = gh.rotation;
            for (int k = 0; k < st.TopLevel.Length; k++)
            {
                var t = p[st.TopLevel[k]];
                t.SetPositionAndRotation(hp + hr * st.AnchorPos[k], hr * st.AnchorRot[k]);
            }
        }
        for (int i = 0; i < p.Length; i++)
        {
            var g = st.Game[i];
            if (g == null) continue;
            p[i].rotation = g.rotation * st.Offsets[i];
            if (i == st.Hips) p[i].position = g.position;
        }
        float hang = Plugin.Cfg.HangingHandAnchor.Value ? HangFromHands(st) : 0f;
        if (Plugin.Cfg.ArmIK.Value)
        {
            ArmIK(st, st.UpperL, st.LowerL, st.HandL, hang);
            ArmIK(st, st.UpperR, st.LowerR, st.HandR, hang);
        }
        if (st.Jiggle != null && Plugin.Cfg.Jiggle.Value) st.Jiggle.Update(Time.deltaTime);
        HeldItems.Follow(st.Skin.Id, st.Game, p, st.Offsets);
    }

    /// <summary>
    /// Two-bone IK on one proxy arm toward the game hand, weighted by how much the game hand is "interacting":
    /// in front of the chest (table, cards, crank) or raised for the rope (<paramref name="hang"/>).
    /// Keeps the arm's current bend plane and the hand's world rotation; never stretches the arm.
    /// </summary>
    private static void ArmIK(State st, int upper, int lower, int hand, float hang)
    {
        if (upper < 0 || lower < 0 || hand < 0) return;
        var gh = st.Game[hand];
        if (gh == null) return;

        float w = Plugin.Cfg.ArmIKAlways.Value ? 1f : hang;
        if (w < 1f && st.GameChest != null && st.GameThighL != null && st.GameThighR != null)
        {
            Vector3 up = st.GameRoot.up;
            Vector3 right = st.GameThighR.position - st.GameThighL.position;
            Vector3 fwd = Vector3.Cross(right, up);
            if (fwd.sqrMagnitude > 1e-8f)
            {
                float ahead = Vector3.Dot(gh.position - st.GameChest.position, fwd.normalized);
                float f = Mathf.InverseLerp(Plugin.Cfg.ArmIKForwardStart.Value, Plugin.Cfg.ArmIKForwardEnd.Value, ahead);
                w = Mathf.Max(w, f * f * (3f - 2f * f));
            }
        }
        if (w <= 0.001f) return;

        var p = st.Proxies;
        SolveTwoBone(p[upper], p[lower], p[hand], gh.position, w);
    }

    /// <summary>Shared two-bone solver (also used by the SDK preview, kept in sync by hand).</summary>
    internal static void SolveTwoBone(Transform upper, Transform lower, Transform hand, Vector3 target, float weight)
    {
        Quaternion handRot = hand.rotation;
        Vector3 s = upper.position, e = lower.position, h = hand.position;
        float a = Vector3.Distance(s, e), b = Vector3.Distance(e, h);
        if (a < 1e-5f || b < 1e-5f) return;

        Vector3 toT = target - s;
        float d = Mathf.Clamp(toT.magnitude, Mathf.Abs(a - b) + 1e-4f, a + b - 1e-4f);
        Vector3 u = toT.sqrMagnitude > 1e-10f ? toT.normalized : (h - s).normalized;

        // bend direction: keep the current elbow side
        Vector3 bend = (e - s) - Vector3.Dot(e - s, u) * u;
        if (bend.sqrMagnitude < 1e-10f) bend = Vector3.Cross(u, upper.right);
        if (bend.sqrMagnitude < 1e-10f) bend = Vector3.Cross(u, Vector3.up);
        bend.Normalize();

        float cosA = Mathf.Clamp((a * a + d * d - b * b) / (2f * a * d), -1f, 1f);
        Vector3 elbow = s + u * (a * cosA) + bend * (a * Mathf.Sqrt(1f - cosA * cosA));
        Vector3 wrist = s + u * d;

        var rUpper = Quaternion.FromToRotation(e - s, elbow - s);
        upper.rotation = Quaternion.Slerp(Quaternion.identity, rUpper, weight) * upper.rotation;

        e = lower.position; h = hand.position;
        Vector3 wristW = Vector3.Lerp(h, wrist, weight);
        var rLower = Quaternion.FromToRotation(h - e, wristW - e);
        lower.rotation = rLower * lower.rotation;

        hand.rotation = handRot;
    }

    /// <summary>
    /// Lobby rope pose: when both game hands are above the game head, slide the whole proxy rig so its hands
    /// meet the game's (tied) hands. Blends in by hand height so seated/gameplay poses keep the hip anchor.
    /// </summary>
    private static float HangFromHands(State st)
    {
        if (st.HandL < 0 || st.HandR < 0 || st.GameHead == null) return 0f;
        var gl = st.Game[st.HandL]; var gr = st.Game[st.HandR];
        if (gl == null || gr == null) return 0f;

        Vector3 up = st.GameRoot.up;
        Vector3 head = st.GameHead.position;
        float above = Mathf.Min(Vector3.Dot(gl.position - head, up), Vector3.Dot(gr.position - head, up));
        float w = Mathf.InverseLerp(Plugin.Cfg.HangingBlendStart.Value, Plugin.Cfg.HangingBlendEnd.Value, above);
        if (w <= 0f) return 0f;
        w = w * w * (3f - 2f * w); // smoothstep

        var p = st.Proxies;
        Vector3 gameMid = (gl.position + gr.position) * 0.5f;
        Vector3 proxyMid = (p[st.HandL].position + p[st.HandR].position) * 0.5f;
        Vector3 delta = (gameMid - proxyMid) * w;
        for (int k = 0; k < st.TopLevel.Length; k++) p[st.TopLevel[k]].position += delta;
        return w;
    }

    private static string Clean(string n)
    {
        if (string.IsNullOrEmpty(n)) return "";
        return n.Replace(" (Instance)", "").Replace("(Clone)", "").Trim();
    }

    private static (CustomSkin skin, int[] slots) Lookup(Mesh mesh)
    {
        if (mesh == null) return (null, null);
        if (_followMeshes.TryGetValue(mesh.Pointer, out var s) && s.FollowSlots.TryGetValue(mesh.Pointer, out var sl)) return (s, sl);
        return _followByName.TryGetValue(Clean(mesh.name) + "|" + mesh.vertexCount, out var hit) ? hit : (null, null);
    }

    /// <summary>One-time diagnostics per live renderer (what it is, where it lives, what its bones point at).</summary>
    private static void Report(SkinnedMeshRenderer r, Mesh mesh, CharRig cr, bool follow)
    {
        if (!_reported.Add(r.Pointer)) return;
        var bones = r.bones;
        string first = bones != null && bones.Length > 0 && bones[0] != null ? bones[0].name : "<none>";
        string parent = r.transform.parent != null ? r.transform.parent.name : "<none>";
        string grand = r.transform.parent != null && r.transform.parent.parent != null ? r.transform.parent.parent.name : "<none>";
        Plugin.Logger.LogInfo($"[diag] live renderer '{r.name}' mesh='{mesh.name}' ({mesh.vertexCount}v, bundleObject={_customMeshes.Contains(mesh.Pointer)}) " +
                              $"parent='{parent}'/'{grand}' charRoot='{cr.CharRoot.name}' bones={bones?.Length ?? 0} bone0='{first}' followRig={follow}");
    }

    private static Vector3 V3(float[] a) => a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.zero;
    private static Quaternion Q(float[] a) => a != null && a.Length >= 4 ? new Quaternion(a[0], a[1], a[2], a[3]) : Quaternion.identity;
}

/// <summary>Per-frame driver for a follow rig (registered in IL2CPP via ClassInjector).</summary>
public class FollowRigDriver : MonoBehaviour
{
    public FollowRigDriver(IntPtr ptr) : base(ptr) { }
    private void LateUpdate() => FollowRigs.Drive(Pointer);
}

/// <summary>Makes live custom-skin renderers render like the base game's (layer, rendering layers, culling).</summary>
internal static class SkinRendering
{
    public static void Match(CharacterSkinLogic logic)
    {
        var defaults = logic._defaultRenderers;
        SkinnedMeshRenderer reference = null;
        if (defaults != null)
            for (int i = 0; i < defaults.Count && reference == null; i++) reference = defaults[i];
        if (reference == null) return;

        int layer = reference.gameObject.layer;
        uint mask = reference.renderingLayerMask;
        var current = logic.CurrentSkin;
        if (current == null) return;
        foreach (var kv in current)
        {
            var list = kv.Value;
            if (list == null) continue;
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                if (r == null) continue;
                r.gameObject.layer = layer;
                r.renderingLayerMask = mask;
                r.updateWhenOffscreen = true;
            }
        }
    }
}
