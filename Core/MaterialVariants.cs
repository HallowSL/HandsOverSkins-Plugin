using System;
using System.Collections.Generic;
using HandsOverSkins.API;
using Jaws.Characters;
using Jaws.Utilities.Materials;
using Project.Tools.DictionaryHelp;
using UnityEngine;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace HandsOverSkins.Core;

/// <summary>
/// Prepares a skin's materials: binds game shaders and registers ghost / dead-body replacements.
///
/// The game swaps materials by *name* through CharacterConfig.ghostMaterials / deathMaterials
/// (SerializableDictionary&lt;EMaterialContentType, List&lt;MaterialPair{from,to}&gt;&gt;, consumed by
/// MaterialReplaceLogic). Base skins ship hand-made pairs, e.g.
///   M_Ben_skin_2_cowboy (URP/Lit) -> M_Dead_Ben_skin_2_cowboy (Dissolve/SH_Dissolve_Lit5)
/// For custom skins we add equivalent pairs, cloning the character's own ghost/dead outfit material
/// as a template and copying the skin's textures into it when the skin doesn't provide its own.
/// </summary>
internal static class MaterialVariants
{
    private static readonly HashSet<(IntPtr cfg, IntPtr mat)> _paired = new();
    private static readonly List<Material> _keepAlive = new();

    private static readonly string[] GhostTextures = { "_BaseMap", "_MainTex", "_BumpMap" };
    private static readonly string[] DeadTextures = { "_BaseMap", "_MainTex", "_BumpMap", "_MetallicGlossMap" };
    private static readonly string[] DeadColors = { "_BaseColor" };

    /// <summary>
    /// Record every shader object shipped inside the skin's bundle. Must run for *all* skins before any
    /// binding, otherwise a URP/Lit copy from one bundle could be mistaken for the game's own shader.
    /// </summary>
    public static void MarkBundleShaders(CustomSkin skin)
    {
        if (skin.MaterialsPrepared) return;
        foreach (var (mat, _) in CollectMaterials(skin)) ShaderResolver.MarkBundleShader(mat.shader);
        foreach (var opt in skin.Materials.Values)
        {
            if (opt.Ghost != null) ShaderResolver.MarkBundleShader(opt.Ghost.shader);
            if (opt.Dead != null) ShaderResolver.MarkBundleShader(opt.Dead.shader);
        }
    }

    public static void Prepare(CustomSkin skin, CharacterConfig character)
    {
        var mats = CollectMaterials(skin);

        if (!skin.MaterialsPrepared)
        {
            MarkBundleShaders(skin);
            ShaderResolver.Refresh();

            foreach (var (mat, originalName) in mats)
            {
                skin.Materials.TryGetValue(originalName, out var opt);
                ShaderResolver.Bind(mat, opt?.Shader);
                // Unique names: MaterialReplaceLogic keys its cache by material name.
                if (!mat.name.StartsWith("HOS_", StringComparison.Ordinal))
                    mat.name = $"HOS_{(int)skin.SkinType}_{originalName}";
                mat.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            }
            skin.MaterialsPrepared = true;
        }

        foreach (var (mat, originalName) in mats)
        {
            if (!_paired.Add((character.Pointer, mat.Pointer))) continue;
            skin.Materials.TryGetValue(originalName, out var opt);

            var ghost = opt?.Ghost;
            if (ghost != null) ShaderResolver.Bind(ghost, null);
            else if ((opt?.AutoGhost ?? true) && Plugin.Cfg.AutoGhostMaterials.Value)
                ghost = MakeVariant(mat, character.ghostMaterials, "Ghost", GhostTextures, Array.Empty<string>());

            var dead = opt?.Dead;
            if (dead != null) ShaderResolver.Bind(dead, null);
            else if ((opt?.AutoDead ?? true) && Plugin.Cfg.AutoDeadMaterials.Value)
                dead = MakeVariant(mat, character.deathMaterials, "Dead", DeadTextures, DeadColors);

            if (ghost != null) AddPair(character.ghostMaterials, mat, ghost);
            if (dead != null) AddPair(character.deathMaterials, mat, dead);
            MaterialStates.Register(mat, ghost, dead); // the game's replace cache is usually built before these pairs exist
        }
    }

    /// <summary>All distinct materials used by the skin's renderers, with their name as authored.</summary>
    private static List<(Material mat, string originalName)> CollectMaterials(CustomSkin skin)
    {
        var seen = new HashSet<IntPtr>();
        var list = new List<(Material, string)>();
        foreach (var renderers in skin.BodyParts.Values)
        foreach (var r in renderers)
        {
            if (r == null) continue;
            foreach (var m in r.sharedMaterials)
            {
                if (m == null || !seen.Add(m.Pointer)) continue;
                string n = m.name;
                string prefix = $"HOS_{(int)skin.SkinType}_";
                list.Add((m, n.StartsWith(prefix, StringComparison.Ordinal) ? n.Substring(prefix.Length) : n));
            }
        }
        return list;
    }

    private static Material MakeVariant(Material alive,
        SerializableDictionary<EMaterialContentType, Il2CppList.List<MaterialPair>> table,
        string suffix, string[] textures, string[] colors)
    {
        var template = PickTemplate(table);
        if (template == null) return null;

        var m = new Material(template) { name = alive.name + "_" + suffix };
        m.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        foreach (var p in textures)
        {
            if (!alive.HasProperty(p) || !m.HasProperty(p)) continue;
            m.SetTexture(p, alive.GetTexture(p));
            m.SetTextureScale(p, alive.GetTextureScale(p));
            m.SetTextureOffset(p, alive.GetTextureOffset(p));
        }
        foreach (var c in colors)
            if (alive.HasProperty(c) && m.HasProperty(c)) m.SetColor(c, alive.GetColor(c));
        _keepAlive.Add(m);
        return m;
    }

    /// <summary>Prefer the character's outfit pair (it carries the right noise/edge settings).</summary>
    private static Material PickTemplate(SerializableDictionary<EMaterialContentType, Il2CppList.List<MaterialPair>> table)
    {
        if (table == null) return null;
        Il2CppList.List<MaterialPair> pairs;
        if (!table.TryGetValue(EMaterialContentType.FullBody, out pairs) || pairs == null || pairs.Count == 0)
            return null;
        Material fallback = null;
        for (int i = 0; i < pairs.Count; i++)
        {
            var p = pairs[i];
            if (p == null || p.to == null) continue;
            fallback ??= p.to;
            var from = p.@from;
            if (from != null && from.name.IndexOf("outfit", StringComparison.OrdinalIgnoreCase) >= 0) return p.to;
        }
        return fallback;
    }

    private static void AddPair(SerializableDictionary<EMaterialContentType, Il2CppList.List<MaterialPair>> table,
        Material from, Material to)
    {
        if (table == null) return;
        foreach (var type in new[] { EMaterialContentType.FullBody, EMaterialContentType.Hands })
        {
            Il2CppList.List<MaterialPair> pairs;
            if (!table.TryGetValue(type, out pairs) || pairs == null)
            {
                if (type != EMaterialContentType.FullBody) continue;
                pairs = new Il2CppList.List<MaterialPair>();
                table.Add(type, pairs);
            }
            var pair = new MaterialPair();
            pair.@from = from;
            pair.to = to;
            pairs.Add(pair);
        }
    }
}
