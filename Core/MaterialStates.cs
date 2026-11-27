using System;
using System.Collections.Generic;
using Jaws.Characters;
using Jaws.Gameplay.Character.Visual;
using Jaws.Utilities.Materials;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace HandsOverSkins.Core;

/// <summary>
/// Ghost / dead-body materials for custom skins.
///
/// The game swaps materials through MaterialReplaceLogic, which builds a name → material cache from
/// CharacterConfig.ghostMaterials/deathMaterials when the CharacterVisualController is constructed. That
/// happens before our pairs exist for most skins (and NormalizeName doesn't always agree with our names), so
/// the cache never hits and custom skins kept their normal materials. Instead we listen to the same two
/// entry points (ReplaceMaterials for ghost on/off, ActivateDeadBody for the corpse) and swap our own
/// materials directly, keeping the mode per CharacterSkinLogic so it survives skin changes and the hidden-part
/// / stump restores (which put the alive materials back).
/// </summary>
internal static class MaterialStates
{
    internal enum Mode { Alive, GhostFull, GhostHands, Dead }

    private sealed class Entry { public Material Alive, Ghost, Dead; }

    private static readonly Dictionary<IntPtr, Entry> _byMat = new();
    private static readonly Dictionary<string, Entry> _byName = new(StringComparer.Ordinal);
    private static readonly Dictionary<IntPtr, (CharacterSkinLogic logic, Mode mode)> _modes = new();
    // alive-body logic -> its visual controller, and which visual controllers belong to a dead character
    // (from CharacterManager.ClientHandleIsDeadChanged), so ghost materials only show while the character is dead
    private static readonly Dictionary<IntPtr, IntPtr> _owners = new();
    private static readonly HashSet<IntPtr> _deadVisuals = new();

    public static void SetDead(CharacterVisualController cvc, bool dead)
    {
        if (cvc == null) return;
        if (dead) _deadVisuals.Add(cvc.Pointer); else _deadVisuals.Remove(cvc.Pointer);
        var logic = cvc._aliveSkinLogic;
        if (logic != null) Reapply(logic);
    }

    public static void Register(Material alive, Material ghost, Material dead)
    {
        if (alive == null) return;
        if (!_byMat.TryGetValue(alive.Pointer, out var e))
        {
            e = new Entry { Alive = alive };
            _byMat[alive.Pointer] = e;
            _byName[alive.name] = e;
        }
        if (ghost != null) { e.Ghost = ghost; _byMat[ghost.Pointer] = e; _byName[ghost.name] = e; }
        if (dead != null) { e.Dead = dead; _byMat[dead.Pointer] = e; _byName[dead.name] = e; }
    }

    private static Entry Find(Material m)
    {
        if (m == null) return null;
        if (_byMat.TryGetValue(m.Pointer, out var e)) return e;
        string n = m.name;
        int i = n.IndexOf(" (Instance)", StringComparison.Ordinal);
        if (i >= 0) n = n.Substring(0, i);
        return _byName.TryGetValue(n, out e) ? e : null;
    }

    public static void Set(CharacterSkinLogic logic, Mode mode)
    {
        if (logic == null) return;
        if (mode == Mode.Alive) _modes.Remove(logic.Pointer);
        else _modes[logic.Pointer] = (logic, mode);
        Apply(logic, mode);
    }

    public static Mode Get(CharacterSkinLogic logic) =>
        logic != null && _modes.TryGetValue(logic.Pointer, out var s) ? s.mode : Mode.Alive;

    /// <summary>After a skin change: put the logic's current mode back on the new renderers.</summary>
    public static void Reapply(CharacterSkinLogic logic)
    {
        if (logic == null || !_modes.TryGetValue(logic.Pointer, out var s)) return;
        Apply(logic, s.mode);
    }

    /// <summary>4 Hz safety net.</summary>
    public static void ReapplyAll()
    {
        if (_modes.Count == 0) return;
        List<IntPtr> dead = null;
        foreach (var kv in _modes)
        {
            var logic = kv.Value.logic;
            if (logic == null || logic.WasCollected || logic._root == null) { (dead ??= new()).Add(kv.Key); continue; }
            Apply(logic, kv.Value.mode);
        }
        if (dead != null) foreach (var p in dead) _modes.Remove(p);
    }

    public static void Forget(CharacterSkinLogic logic)
    {
        if (logic != null) { _modes.Remove(logic.Pointer); _owners.Remove(logic.Pointer); }
    }

    private static void Apply(CharacterSkinLogic logic, Mode mode)
    {
        // The game also calls ReplaceMaterials(Hands/FullBody) on living characters (e.g. the BuzzWire round), which
        // turned custom hands into ghost hands. Ghost materials go on only while the character is dead.
        if ((mode == Mode.GhostFull || mode == Mode.GhostHands) && Plugin.Cfg.GhostOnlyWhenDead.Value
            && !(_owners.TryGetValue(logic.Pointer, out var cvcPtr) && _deadVisuals.Contains(cvcPtr)))
            mode = Mode.Alive;
        if (_byMat.Count == 0) return;
        Transform root;
        try { root = logic._root; } catch { return; }
        if (root == null) return;

        HashSet<IntPtr> hands = null;
        if (mode == Mode.GhostHands) hands = HandRenderers(logic);

        foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (r == null) continue;
            var mats = r.sharedMaterials;
            if (mats == null || mats.Length == 0) continue; // hidden part
            bool hand = hands != null && hands.Contains(r.Pointer);
            bool changed = false;
            var next = new Material[mats.Length];
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                next[i] = m;
                var e = Find(m);
                if (e == null) continue;
                Material want = mode switch
                {
                    Mode.GhostFull => e.Ghost ?? e.Alive,
                    Mode.GhostHands => hand ? e.Ghost ?? e.Alive : e.Alive,
                    Mode.Dead => e.Dead ?? e.Alive,
                    _ => e.Alive,
                };
                if (want != null && want.Pointer != m.Pointer) { next[i] = want; changed = true; }
            }
            if (changed) r.sharedMaterials = new Il2CppReferenceArray<Material>(next);
        }
    }

    private static HashSet<IntPtr> HandRenderers(CharacterSkinLogic logic)
    {
        var set = new HashSet<IntPtr>();
        try
        {
            var cur = logic.CurrentSkin;
            if (cur == null) return set;
            foreach (var part in new[] { ESkinBodyPart.HandL, ESkinBodyPart.HandR })
            {
                Il2CppSystem.Collections.Generic.List<SkinnedMeshRenderer> list;
                if (!cur.TryGetValue(part, out list) || list == null) continue;
                for (int i = 0; i < list.Count; i++) if (list[i] != null) set.Add(list[i].Pointer);
            }
        }
        catch { }
        return set;
    }

    // --- game hooks (called from SkinPatches) ---

    public static void OnReplaceMaterials(CharacterVisualController cvc, EMaterialContentType type)
    {
        var logic = cvc?._aliveSkinLogic;
        if (logic == null) return;
        _owners[logic.Pointer] = cvc.Pointer;
        Set(logic, type switch
        {
            EMaterialContentType.FullBody => Mode.GhostFull,
            EMaterialContentType.Hands => Mode.GhostHands,
            _ => Mode.Alive,
        });
    }

    public static void OnDeadBody(CharacterVisualController cvc)
    {
        var logic = cvc?._deathSkinLogic;
        if (logic != null) Set(logic, Mode.Dead);
    }
}
