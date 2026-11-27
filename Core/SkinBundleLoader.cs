using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HandsOverSkins.API;
using Il2CppInterop.Runtime;
using Jaws.Characters;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HandsOverSkins.Core;

/// <summary>Finds *.hoskin AssetBundles, reads their JSON manifests and turns them into <see cref="CustomSkin"/>s.</summary>
internal static class SkinBundleLoader
{
    public const string Extension = ".hoskin";

    private static readonly List<string> _pending = new();
    private static readonly HashSet<string> _loaded = new(StringComparer.OrdinalIgnoreCase);
    // Keep bundles + assets alive for the whole session.
    private static readonly List<AssetBundle> _bundles = new();

    public static int PendingCount => _pending.Count;

    public static void DiscoverBundles(string root)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*" + Extension, SearchOption.AllDirectories))
                if (!_pending.Contains(file, StringComparer.OrdinalIgnoreCase) && !_loaded.Contains(file))
                    _pending.Add(file);
        }
        catch (Exception e) { Plugin.Logger.LogError($"Scanning '{root}' failed: {e.Message}"); }
    }

    /// <summary>Loads everything that was discovered. Called right before the first injection.</summary>
    public static void LoadPending()
    {
        if (_pending.Count == 0) return;
        var files = _pending.ToArray();
        _pending.Clear();
        foreach (var f in files) LoadBundle(f);
    }

    public static IReadOnlyList<CustomSkin> LoadBundle(string path)
    {
        var result = new List<CustomSkin>();
        path = Path.GetFullPath(path);
        if (!_loaded.Add(path)) return result;

        AssetBundle bundle = OpenBundle(path);
        if (bundle == null) { Plugin.Logger.LogError($"'{path}' is not a valid AssetBundle (built for a different Unity version?)."); return result; }
        _bundles.Add(bundle);

        var manifests = new List<SkinManifest>();
        foreach (var obj in LoadAll(bundle, Il2CppType.Of<TextAsset>()))
        {
            var ta = obj.TryCast<TextAsset>();
            if (ta == null) continue;
            string text = ta.text;
            if (string.IsNullOrEmpty(text) || !text.Contains("\"hoskinFormat\"")) continue;
            try { manifests.Add(JsonSerializer.Deserialize<SkinManifest>(text, SkinManifest.JsonOptions)); }
            catch (Exception e) { Plugin.Logger.LogError($"Bad manifest '{ta.name}' in {Path.GetFileName(path)}: {e.Message}"); }
        }

        if (manifests.Count == 0)
        {
            Plugin.Logger.LogWarning($"{Path.GetFileName(path)} contains no skin manifest. Build it with the HANDS OVER Skin SDK.");
            return result;
        }

        foreach (var m in manifests)
        {
            try
            {
                var skin = Build(m, bundle, path);
                if (skin != null && SkinAPI.RegisterSkin(skin)) result.Add(skin);
            }
            catch (Exception e) { Plugin.Logger.LogError($"Skin '{m?.Id}' in {Path.GetFileName(path)} failed: {e}"); }
        }
        return result;
    }

    private static CustomSkin Build(SkinManifest m, AssetBundle bundle, string path)
    {
        if (m.HoskinFormat > 1)
            Plugin.Logger.LogWarning($"Skin '{m.Id}' was built with a newer SDK (format {m.HoskinFormat}). Update HandsOverSkins.");

        if (!Enum.TryParse(m.Character, true, out ECharacterType character) || character == ECharacterType.None)
        {
            Plugin.Logger.LogError($"Skin '{m.Id}': unknown character '{m.Character}'. Use one of: Ben, Garp, Gloria, Lisa, Livia, Lucas.");
            return null;
        }

        var prefab = Find<GameObject>(bundle, m.Prefab);
        if (prefab == null) { Plugin.Logger.LogError($"Skin '{m.Id}': prefab '{m.Prefab}' not found in bundle."); return null; }
        prefab.hideFlags |= HideFlags.DontUnloadUnusedAsset;

        var skin = new CustomSkin
        {
            Id = m.Id,
            DisplayName = string.IsNullOrWhiteSpace(m.DisplayName) ? m.Id : m.DisplayName,
            Description = m.Description ?? "",
            Author = string.IsNullOrWhiteSpace(m.Author) ? "Unknown" : m.Author,
            Version = m.Version ?? "1.0.0",
            Character = character,
            RequiredLevel = Math.Max(1, m.RequiredLevel),
            Order = m.Order,
            Prefab = prefab,
            Source = path,
        };
        foreach (var kv in m.LocalizedNames ?? new()) skin.LocalizedNames[kv.Key] = kv.Value;
        foreach (var kv in m.LocalizedDescriptions ?? new()) skin.LocalizedDescriptions[kv.Key] = kv.Value;

        // Body parts
        var allRenderers = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true).ToArray();
        foreach (var bp in m.BodyParts ?? new())
        {
            // Stumps aren't a game body part: they ride along in the Outfit list (so the game instantiates and rigs them)
            // and the plugin only shows them while that hand is cut off.
            if (string.Equals(bp.Part, "StumpL", StringComparison.OrdinalIgnoreCase) || string.Equals(bp.Part, "StumpR", StringComparison.OrdinalIgnoreCase))
            {
                bool right = bp.Part.EndsWith("R", StringComparison.OrdinalIgnoreCase);
                if (!skin.BodyParts.TryGetValue(ESkinBodyPart.Outfit, out var outfit)) skin.BodyParts[ESkinBodyPart.Outfit] = outfit = new List<SkinnedMeshRenderer>();
                foreach (var r in bp.Renderers ?? new())
                {
                    var smr = FindRenderer(prefab.transform, allRenderers, r);
                    if (smr == null || smr.sharedMesh == null) { Plugin.Logger.LogWarning($"Skin '{m.Id}': stump renderer '{r}' not found in prefab."); continue; }
                    if (!outfit.Contains(smr)) outfit.Add(smr);
                    skin.StumpMeshes[smr.sharedMesh.Pointer] = right;
                }
                continue;
            }
            if (!Enum.TryParse(bp.Part, true, out ESkinBodyPart part))
            { Plugin.Logger.LogWarning($"Skin '{m.Id}': unknown body part '{bp.Part}' skipped."); continue; }
            if (!skin.BodyParts.TryGetValue(part, out var list)) skin.BodyParts[part] = list = new List<SkinnedMeshRenderer>();
            foreach (var r in bp.Renderers ?? new())
            {
                var smr = FindRenderer(prefab.transform, allRenderers, r);
                if (smr == null) Plugin.Logger.LogWarning($"Skin '{m.Id}': renderer '{r}' for {part} not found in prefab.");
                else if (!list.Contains(smr)) list.Add(smr);
            }
        }
        foreach (var h in m.HeldItems ?? new())
            if (h != null && !string.IsNullOrWhiteSpace(h.Target)) skin.HeldItems.Add(h);
        foreach (var hp in m.HiddenParts ?? new())
            if (Enum.TryParse(hp, true, out ESkinBodyPart part)) skin.HiddenParts.Add(part);

        // Materials
        foreach (var me in m.Materials ?? new())
        {
            if (string.IsNullOrEmpty(me.Material)) continue;
            skin.Materials[me.Material] = new MaterialOptions
            {
                Shader = me.Shader,
                Ghost = string.IsNullOrEmpty(me.Ghost) ? null : Find<Material>(bundle, me.Ghost),
                Dead = string.IsNullOrEmpty(me.Dead) ? null : Find<Material>(bundle, me.Dead),
                AutoGhost = me.AutoGhost,
                AutoDead = me.AutoDead,
            };
        }

        // Follow rig (Humanoid skins that keep their own skeleton)
        if (m.FollowRig != null && m.FollowRig.Bones.Count > 0)
        {
            skin.FollowRig = m.FollowRig;
            foreach (var fr in m.FollowRig.Renderers)
            {
                var smr = FindRenderer(prefab.transform, allRenderers, fr.Renderer);
                var mesh = smr != null ? smr.sharedMesh : null;
                if (mesh == null) { Plugin.Logger.LogWarning($"Skin '{m.Id}': follow-rig renderer '{fr.Renderer}' not found."); continue; }
                if (fr.Bones.Any(i => i < 0 || i >= m.FollowRig.Bones.Count))
                { Plugin.Logger.LogError($"Skin '{m.Id}': follow-rig renderer '{fr.Renderer}' references a missing bone."); continue; }
                skin.FollowSlots[mesh.Pointer] = fr.Bones.ToArray();
            }
        }

        // Portrait
        if (!string.IsNullOrEmpty(m.Portrait))
        {
            var sprite = Find<Sprite>(bundle, m.Portrait);
            if (sprite == null)
            {
                var tex = Find<Texture2D>(bundle, m.Portrait);
                if (tex != null)
                {
                    sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 20f);
                    sprite.name = m.Id + "_portrait";
                }
            }
            if (sprite != null) { sprite.hideFlags |= HideFlags.DontUnloadUnusedAsset; skin.Portrait = sprite; }
            else Plugin.Logger.LogWarning($"Skin '{m.Id}': portrait '{m.Portrait}' not found, the default card will be used.");
        }
        return skin;
    }

    private static SkinnedMeshRenderer FindRenderer(Transform root, SkinnedMeshRenderer[] all, string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var t = root.Find(key);
        if (t != null)
        {
            var smr = t.GetComponent<SkinnedMeshRenderer>();
            if (smr != null) return smr;
        }
        string leaf = key.Contains('/') ? key.Substring(key.LastIndexOf('/') + 1) : key;
        return all.FirstOrDefault(r => string.Equals(r.name, leaf, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------------------------------------
    // HANDS OVER's IL2CPP build stripped AssetBundle.LoadFromFile / LoadAllAssets / GetAllAssetNames
    // (the game only uses Addressables). Il2CppInterop "unstrips" them from Unity 6's managed code, but
    // those bodies need span helpers the game doesn't ship ("Method not found ... ReadOnlySpan ...").
    // So we only call members that really exist in GameAssembly.dll:
    //   LoadFromFileAsync(string) / LoadFromMemory(byte[]), LoadAsset(string, Type), LoadAllAssetsAsync(Type).
    // Reading .assetBundle / .allAssets on a pending request completes it synchronously.
    // ---------------------------------------------------------------------------------------------
    private static AssetBundle OpenBundle(string path)
    {
        try
        {
            var req = AssetBundle.LoadFromFileAsync(path);
            var b = req?.assetBundle;
            if (b != null) return b;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"LoadFromFileAsync failed for '{Path.GetFileName(path)}' ({e.Message}); trying LoadFromMemory."); }

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            var b = AssetBundle.LoadFromMemory(bytes);
            if (b != null) return b;
        }
        catch (Exception e) { Plugin.Logger.LogError($"Could not open '{path}': {e.Message}"); }
        return null;
    }

    private static Object[] LoadAll(AssetBundle bundle, Il2CppSystem.Type type)
    {
        try
        {
            var req = bundle.LoadAllAssetsAsync(type);
            var all = req?.allAssets;
            return all == null ? Array.Empty<Object>() : all.ToArray();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Reading bundle '{bundle.name}' failed: {e.Message}");
            return Array.Empty<Object>();
        }
    }

    /// <summary>Resolve an asset by full path, file name or object name.</summary>
    internal static T Find<T>(AssetBundle bundle, string key) where T : Object
    {
        if (string.IsNullOrEmpty(key)) return null;
        var type = Il2CppType.Of<T>();
        Object o = null;
        try { o = bundle.LoadAsset(key, type); } catch { /* not a path */ }
        if (o == null)
        {
            // Fall back to matching object names (file name without extension, or sub-asset name).
            string k = key.Replace('\\', '/');
            string leaf = Path.GetFileNameWithoutExtension(k.Contains('/') ? k.Substring(k.LastIndexOf('/') + 1) : k);
            foreach (var a in LoadAll(bundle, type))
                if (a != null && (string.Equals(a.name, key, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(a.name, leaf, StringComparison.OrdinalIgnoreCase))) { o = a; break; }
        }
        return o == null ? null : o.TryCast<T>();
    }
}
