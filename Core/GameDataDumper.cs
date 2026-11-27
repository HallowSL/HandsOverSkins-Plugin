using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;
using Il2CppInterop.Runtime;
using Jaws.Characters;
using Jaws.Providers;
using UnityEngine;

namespace HandsOverSkins.Core;

/// <summary>
/// Developer tool (config: Developer/DumpGameData). Writes what the SDK needs to stay in sync with the game:
///   skins.json   - every base skin: character, skin number, order, level, description key, body parts, materials
///   rigs.json    - every character's rig hierarchy (bone paths), the contract for skin meshes
///   shaders.json - every loaded shader with its property names/types (to make SDK proxy shaders)
/// Copy rigs.json / shaders.json into the SDK's Assets/HandsOverSkinSDK/Data folder to refresh it.
/// </summary>
internal static class GameDataDumper
{
    public static void Dump(CharactersProvider provider)
    {
        string dir = Path.Combine(Paths.BepInExRootPath, "HandsOverSkins_Dump");
        Directory.CreateDirectory(dir);
        var opts = new JsonSerializerOptions { WriteIndented = true };

        var skins = new List<object>();
        var rigs = new Dictionary<string, object>();
        foreach (ECharacterType ct in Enum.GetValues(typeof(ECharacterType)))
        {
            if (ct == ECharacterType.None) continue;
            // GetSkins() returns Il2CppSystem IReadOnlyList<T>, whose interop wrapper exposes no Count/indexer.
            // Read the provider's backing Dictionary<ECharacterType, List<CharacterSkinConfig>> instead.
            Il2CppSystem.Collections.Generic.List<CharacterSkinConfig> list;
            if (!provider._skins.TryGetValue(ct, out list) || list == null) continue;
            for (int i = 0; i < list.Count; i++)
            {
                var cfg = list[i];
                if (cfg == null) continue;
                var parts = new Dictionary<string, object>();
                foreach (var kv in cfg.bodyParts)
                {
                    var rs = new List<object>();
                    for (int j = 0; j < kv.Value.Count; j++)
                    {
                        var r = kv.Value[j];
                        if (r == null) continue;
                        rs.Add(new
                        {
                            renderer = r.name,
                            prefab = r.transform.root.name,
                            mesh = r.sharedMesh != null ? r.sharedMesh.name : null,
                            rootBone = r.rootBone != null ? r.rootBone.name : null,
                            boneCount = r.bones.Length,
                            materials = r.sharedMaterials.Where(m => m != null)
                                .Select(m => new { name = m.name, shader = m.shader != null ? m.shader.name : null }).ToList(),
                        });
                    }
                    parts[kv.Key.ToString()] = rs;
                }
                string descKey = null;
                try { descKey = cfg.description?.TableEntryReference.Key; } catch { /* ignored */ }
                skins.Add(new
                {
                    asset = cfg.name,
                    character = ct.ToString(),
                    skinType = (int)cfg.skinType,
                    order = cfg.order,
                    requiredLevel = cfg.requiredLevel,
                    descriptionKey = descKey,
                    portrait = cfg.wardrobeCardSprite != null ? cfg.wardrobeCardSprite.name : null,
                    bodyParts = parts,
                });

                if (cfg.skinType == ESkinType.Default && !rigs.ContainsKey(ct.ToString()))
                {
                    SkinnedMeshRenderer any = null;
                    foreach (var kv in cfg.bodyParts)
                        if (kv.Value.Count > 0 && kv.Value[0] != null) { any = kv.Value[0]; break; }
                    if (any != null)
                    {
                        var bones = new List<string>();
                        Walk(any.transform.root, "", bones);
                        rigs[ct.ToString()] = new { prefab = any.transform.root.name, rootBone = any.rootBone != null ? any.rootBone.name : null, transforms = bones };
                    }
                }
            }
        }

        // Shader property enumeration (GetPropertyCount/Name/Type) is stripped from the game build,
        // so only names are dumped. The SDK's proxy shaders already carry the full property blocks.
        var shaders = new List<string>();
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Shader>()))
        {
            var s = o.TryCast<Shader>();
            if (s != null && !string.IsNullOrEmpty(s.name)) shaders.Add(s.name);
        }

        File.WriteAllText(Path.Combine(dir, "skins.json"), JsonSerializer.Serialize(skins, opts));
        File.WriteAllText(Path.Combine(dir, "rigs.json"), JsonSerializer.Serialize(rigs, opts));
        File.WriteAllText(Path.Combine(dir, "shaders.json"), JsonSerializer.Serialize(shaders.Distinct().OrderBy(s => s).ToList(), opts));
        Plugin.Logger.LogInfo($"Game data dumped to {dir}");
    }

    private static void Walk(Transform t, string prefix, List<string> outList)
    {
        string path = string.IsNullOrEmpty(prefix) ? t.name : prefix + "/" + t.name;
        outList.Add(path);
        for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), path, outList);
    }
}
