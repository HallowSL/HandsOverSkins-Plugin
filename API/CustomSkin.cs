using System;
using System.Collections.Generic;
using Jaws.Characters;
using UnityEngine;

namespace HandsOverSkins.API;

/// <summary>
/// A custom skin ready to be injected into the game. Created automatically from .hoskin bundles,
/// or build one in code and pass it to <see cref="SkinAPI.RegisterSkin"/>.
/// </summary>
public sealed class CustomSkin
{
    public string Id;
    public string DisplayName;
    public string Description;
    public string Author = "Unknown";
    public string Version = "1.0.0";
    public readonly Dictionary<string, string> LocalizedNames = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> LocalizedDescriptions = new(StringComparer.OrdinalIgnoreCase);

    public ECharacterType Character;
    public int RequiredLevel = 1;
    public int Order = 100;

    /// <summary>Prefab that contains the skin's SkinnedMeshRenderers and a skeleton whose bone names match the character rig.</summary>
    public GameObject Prefab;
    /// <summary>Wardrobe card portrait (game uses 288x512, 20 ppu). Falls back to the character's default card.</summary>
    public Sprite Portrait;

    public readonly Dictionary<ESkinBodyPart, List<SkinnedMeshRenderer>> BodyParts = new();
    public readonly HashSet<ESkinBodyPart> HiddenParts = new();
    /// <summary>Stump meshes (Mesh.Pointer → true = right wrist). Shown only while that hand is cut off.</summary>
    public readonly Dictionary<System.IntPtr, bool> StumpMeshes = new();
    /// <summary>Per-material options keyed by material name.</summary>
    public readonly Dictionary<string, MaterialOptions> Materials = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// "Follow Rig" data for Humanoid skins that keep their own proportions (null for normal skins).
    /// <see cref="FollowSlots"/> maps each renderer's mesh to proxy bone indices.
    /// </summary>
    public FollowRigData FollowRig;
    public readonly Dictionary<System.IntPtr, int[]> FollowSlots = new(); // keyed by Mesh.Pointer (Il2Cpp wrappers are not reference-stable)

    /// <summary>Held-item offsets (card holders etc.), applied while the skin is worn.</summary>
    public readonly List<HeldItemEntry> HeldItems = new();

    /// <summary>True for the plugin's "Random" card: applying it picks one of the character's other skins.</summary>
    public bool IsRandom { get; internal set; }

    /// <summary>Where this skin came from (bundle path or the registering assembly).</summary>
    public string Source;

    // ---- Filled in by the plugin ----
    /// <summary>Stable numeric skin type sent over the network (derived from <see cref="Id"/>).</summary>
    public ESkinType SkinType { get; internal set; }
    /// <summary>The game-side config object created for this skin (null until injected).</summary>
    public CharacterSkinConfig Config { get; internal set; }
    internal string LocalizationKey => "HOSKIN_" + ((int)SkinType).ToString() + "_DESC";
    internal bool MaterialsPrepared;

    public string GetName(string localeCode) => Pick(LocalizedNames, localeCode) ?? DisplayName ?? Id;
    public string GetDescription(string localeCode) => Pick(LocalizedDescriptions, localeCode) ?? Description ?? string.Empty;

    /// <summary>Text shown in the wardrobe description panel (format comes from the plugin config).</summary>
    public string FormatDescription(string localeCode)
    {
        string fmt = Plugin.Cfg?.DescriptionFormat.Value ?? "<b>{name}</b>\n{description}";
        return fmt.Replace("{name}", GetName(localeCode))
                  .Replace("{description}", GetDescription(localeCode))
                  .Replace("{author}", Author ?? "")
                  .Replace("{version}", Version ?? "")
                  .Replace("{character}", Character.ToString())
                  .Replace("\\n", "\n");
    }

    private static string Pick(Dictionary<string, string> map, string code)
    {
        if (string.IsNullOrEmpty(code) || map.Count == 0) return null;
        if (map.TryGetValue(code, out var v) && !string.IsNullOrEmpty(v)) return v;
        int dash = code.IndexOf('-');
        if (dash > 0 && map.TryGetValue(code.Substring(0, dash), out v) && !string.IsNullOrEmpty(v)) return v;
        return null;
    }

    public override string ToString() => $"{Id} ({Character}, skin #{(int)SkinType})";
}

public sealed class MaterialOptions
{
    /// <summary>Name of the game shader to bind (empty = keep the shader name the material already has).</summary>
    public string Shader;
    public Material Ghost;
    public Material Dead;
    public bool AutoGhost = true;
    public bool AutoDead = true;
}
