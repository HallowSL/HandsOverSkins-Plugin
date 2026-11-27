using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace HandsOverSkins.Core;

/// <summary>
/// Re-binds bundle materials to the game's own shader objects.
///
/// Materials authored in the SDK reference either a real shader (URP/Lit, compiled into the bundle)
/// or a "proxy" stub that only carries the game shader's name + property block
/// (e.g. "Dissolve/SH_Dissolve_Lit5", "MS/Ghost Unlit"). At runtime we look up the game's
/// shader with that name and swap it in, so materials render exactly like the base game
/// (keywords, URP variants, SRP batcher compatible, dissolve/ghost effects).
///
/// Game shaders seen on characters:
///   Universal Render Pipeline/Lit      - alive skins
///   Dissolve/SH_Dissolve_Lit5          - dead body (dissolve)
///   MS/Ghost Unlit                     - ghost
///   Shader Graphs/SG_AStencilLIt5      - jaw/teeth stencil blocker
/// </summary>
internal static class ShaderResolver
{
    private static readonly Dictionary<string, Shader> _game = new(StringComparer.Ordinal);
    private static readonly HashSet<IntPtr> _bundleShaders = new();
    private static readonly List<(Material mat, string name)> _unresolved = new();
    private static float _lastRefresh = -999f;

    /// <summary>Remember a shader that came from a skin bundle so it is never mistaken for the game's copy.</summary>
    public static void MarkBundleShader(Shader s)
    {
        if (s != null) _bundleShaders.Add(s.Pointer);
    }

    public static void Refresh()
    {
        _game.Clear();
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Shader>()))
        {
            var s = o.TryCast<Shader>();
            if (s == null || _bundleShaders.Contains(s.Pointer)) continue;
            string n = s.name;
            if (string.IsNullOrEmpty(n) || _game.ContainsKey(n)) continue;
            _game[n] = s;
        }
        _lastRefresh = Time.realtimeSinceStartup;
    }

    public static Shader Get(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_game.TryGetValue(name, out var s) && s != null) return s;
        var f = Shader.Find(name);
        if (f != null && !_bundleShaders.Contains(f.Pointer)) { _game[name] = f; return f; }
        return null;
    }

    /// <summary>Bind <paramref name="mat"/> to the game shader named <paramref name="overrideName"/> (or its own shader name).</summary>
    public static bool Bind(Material mat, string overrideName)
    {
        if (mat == null) return false;
        string target = string.IsNullOrEmpty(overrideName) ? mat.shader != null ? mat.shader.name : null : overrideName;
        if (string.IsNullOrEmpty(target)) return false;

        var game = Get(target);
        if (game == null)
        {
            _unresolved.Add((mat, target));
            if (Plugin.Cfg.VerboseLogging.Value)
                Plugin.Logger.LogInfo($"Shader '{target}' for material '{mat.name}' not loaded yet, keeping bundle shader for now.");
            return false;
        }
        if (mat.shader == null || mat.shader.Pointer != game.Pointer) Swap(mat, game);
        return true;
    }

    /// <summary>Some game shaders only load with a level; retry the leftovers occasionally.</summary>
    public static void RetryUnresolved()
    {
        if (_unresolved.Count == 0) return;
        if (Time.realtimeSinceStartup - _lastRefresh < 2f) return;
        Refresh();
        for (int i = _unresolved.Count - 1; i >= 0; i--)
        {
            var (mat, name) = _unresolved[i];
            if (mat == null || mat.WasCollected) { _unresolved.RemoveAt(i); continue; }
            var game = Get(name);
            if (game == null) continue;
            Swap(mat, game);
            _unresolved.RemoveAt(i);
            Plugin.Logger.LogInfo($"Late-bound material '{mat.name}' to game shader '{name}'.");
        }
    }

    // Material.renderQueue / Shader.renderQueue getters are stripped from the game build, so the
    // material keeps whatever queue the game shader assigns (same as the base-game skins).
    private static void Swap(Material mat, Shader game) => mat.shader = game;

    public static IEnumerable<KeyValuePair<string, Shader>> GameShaders => _game;
}
