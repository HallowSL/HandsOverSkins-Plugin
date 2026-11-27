using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HandsOverSkins.API;

/// <summary>
/// JSON contract written by the Unity SDK into every .hoskin bundle (as a TextAsset).
/// IL2CPP games cannot deserialize MonoBehaviours/ScriptableObjects from mod assemblies,
/// so all skin metadata travels as JSON and the bundle itself only contains plain Unity assets
/// (prefab with SkinnedMeshRenderers, materials, textures, portrait).
/// </summary>
public sealed class SkinManifest
{
    /// <summary>Marker + version. Any TextAsset whose JSON has this key is treated as a skin manifest.</summary>
    [JsonPropertyName("hoskinFormat")] public int HoskinFormat { get; set; } = 1;

    /// <summary>Globally unique, stable id (e.g. "yourname.ben_pirate"). Drives the networked skin number.</summary>
    [JsonPropertyName("id")] public string Id { get; set; }
    [JsonPropertyName("displayName")] public string DisplayName { get; set; }
    [JsonPropertyName("description")] public string Description { get; set; }
    [JsonPropertyName("author")] public string Author { get; set; }
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";

    /// <summary>Optional translations keyed by locale code ("fr", "de", "pt-BR", ...).</summary>
    [JsonPropertyName("localizedNames")] public Dictionary<string, string> LocalizedNames { get; set; } = new();
    [JsonPropertyName("localizedDescriptions")] public Dictionary<string, string> LocalizedDescriptions { get; set; } = new();

    /// <summary>Ben, Garp, Gloria, Lisa, Livia or Lucas (Jaws.Characters.ECharacterType).</summary>
    [JsonPropertyName("character")] public string Character { get; set; }
    [JsonPropertyName("requiredLevel")] public int RequiredLevel { get; set; } = 1;
    /// <summary>Sort order in the wardrobe. Game skins use 0..3, custom skins default to 100+.</summary>
    [JsonPropertyName("order")] public int Order { get; set; } = 100;

    /// <summary>Asset path (or name) of the skin prefab inside the bundle.</summary>
    [JsonPropertyName("prefab")] public string Prefab { get; set; }
    /// <summary>Asset path (or name) of the 288x512 wardrobe card portrait (Texture2D or Sprite). Optional.</summary>
    [JsonPropertyName("portrait")] public string Portrait { get; set; }

    [JsonPropertyName("bodyParts")] public List<BodyPartEntry> BodyParts { get; set; } = new();
    /// <summary>Default body parts to hide without replacing them (e.g. "Hair" under a hat).</summary>
    [JsonPropertyName("hiddenParts")] public List<string> HiddenParts { get; set; } = new();
    /// <summary>Offsets for held items (card holders Cards_left / Cards_right, …) for bigger hands or paws.</summary>
    [JsonPropertyName("heldItems")] public List<HeldItemEntry> HeldItems { get; set; } = new();
    [JsonPropertyName("materials")] public List<MaterialEntry> Materials { get; set; } = new();

    /// <summary>
    /// Optional. Present for skins made from a Humanoid model in "Follow Rig" mode: the model keeps its own
    /// skeleton (proportions), and at runtime every proxy bone copies the rotation of its game bone.
    /// </summary>
    [JsonPropertyName("followRig")] public FollowRigData FollowRig { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };
}

public sealed class BodyPartEntry
{
    /// <summary>Hat, Hair, Outfit, HandL, HandR, GloveL, GloveR, Head, Body (Jaws.Characters.ESkinBodyPart).</summary>
    [JsonPropertyName("part")] public string Part { get; set; }
    /// <summary>Transform paths (relative to the prefab root) or names of SkinnedMeshRenderers.</summary>
    [JsonPropertyName("renderers")] public List<string> Renderers { get; set; } = new();
}

public sealed class MaterialEntry
{
    /// <summary>Material asset name as used by the renderers.</summary>
    [JsonPropertyName("material")] public string Material { get; set; }
    /// <summary>Game shader to bind at runtime. Empty = use the material's own shader name.</summary>
    [JsonPropertyName("shader")] public string Shader { get; set; }
    /// <summary>Optional material asset name used while the player is a ghost.</summary>
    [JsonPropertyName("ghost")] public string Ghost { get; set; }
    /// <summary>Optional material asset name used on the dead body (dissolve).</summary>
    [JsonPropertyName("dead")] public string Dead { get; set; }
    [JsonPropertyName("autoGhost")] public bool AutoGhost { get; set; } = true;
    [JsonPropertyName("autoDead")] public bool AutoDead { get; set; } = true;
}

/// <summary>Proxy skeleton for "Follow Rig" skins (see <see cref="SkinManifest.FollowRig"/>).</summary>
public sealed class FollowRigData
{
    /// <summary>Parent-first list. Rest pose is the model posed into the game rig's rest pose.</summary>
    [JsonPropertyName("bones")] public List<FollowBone> Bones { get; set; } = new();
    /// <summary>Per renderer: bone slot i of its mesh → index into <see cref="Bones"/>.</summary>
    [JsonPropertyName("renderers")] public List<FollowRenderer> Renderers { get; set; } = new();
    /// <summary>Spring ("jiggle") bone groups. Optional.</summary>
    [JsonPropertyName("jiggle")] public List<FollowJiggle> Jiggle { get; set; } = new();
}

public sealed class FollowJiggle
{
    /// <summary>Indices into <see cref="FollowRigData.Bones"/>, parent-first, none of them following a game bone.</summary>
    [JsonPropertyName("bones")] public List<int> Bones { get; set; } = new();
    /// <summary>Per bone: rest tip (child offset) in the bone's local space, x,y,z flattened.</summary>
    [JsonPropertyName("tips")] public List<float> Tips { get; set; } = new();
    [JsonPropertyName("stiffness")] public float Stiffness { get; set; } = 0.35f;
    [JsonPropertyName("damping")] public float Damping { get; set; } = 0.25f;
    [JsonPropertyName("gravity")] public float Gravity { get; set; }
}

public sealed class FollowBone
{
    [JsonPropertyName("name")] public string Name { get; set; }
    /// <summary>Index of the parent proxy bone, or -1 for the game rig's "root" transform.</summary>
    [JsonPropertyName("parent")] public int Parent { get; set; } = -1;
    /// <summary>Local position (x,y,z) at rest.</summary>
    [JsonPropertyName("p")] public float[] P { get; set; }
    /// <summary>Local rotation (x,y,z,w) at rest.</summary>
    [JsonPropertyName("r")] public float[] R { get; set; }
    /// <summary>Game bone whose rotation this bone follows (null = keeps its rest rotation relative to its parent).</summary>
    [JsonPropertyName("gameBone")] public string GameBone { get; set; }
    /// <summary>Rotation offset: proxy world rotation = game bone world rotation × offset.</summary>
    [JsonPropertyName("offset")] public float[] Offset { get; set; }
}

public sealed class FollowRenderer
{
    [JsonPropertyName("renderer")] public string Renderer { get; set; }
    [JsonPropertyName("bones")] public List<int> Bones { get; set; } = new();
}

public sealed class HeldItemEntry
{
    /// <summary>Name of a transform in the game character (e.g. Cards_left).</summary>
    [JsonPropertyName("target")] public string Target { get; set; }
    /// <summary>Position offset (x,y,z) in the target's own axes and units.</summary>
    [JsonPropertyName("p")] public float[] P { get; set; }
    /// <summary>Rotation offset, Euler degrees (x,y,z), in the target's own axes.</summary>
    [JsonPropertyName("r")] public float[] R { get; set; }
    [JsonPropertyName("s")] public float S { get; set; } = 1f;
    /// <summary>
    /// Follow Rig skins: the offset was authored against the model's own hand (A-pose in the SDK Workbench).
    /// The holder then follows the model's hand bone instead of the game hand, keeping the same placement relative to it.
    /// </summary>
    [JsonPropertyName("followSkinHand")] public bool FollowSkinHand { get; set; }
}
