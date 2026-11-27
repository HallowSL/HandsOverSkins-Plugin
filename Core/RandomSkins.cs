using System;
using System.Collections.Generic;
using HandsOverSkins.API;
using Jaws.Characters;
using Jaws.Providers;
using Jaws.Wardrobe;
using UnityEngine;

namespace HandsOverSkins.Core;

/// <summary>
/// "Random" wardrobe card per character. It is a mesh-less pseudo skin with its own skin number; whenever that number
/// is about to be used (the local player's chosen skin, or a character being dressed in it), a real skin of that
/// character is substituted. The pick is made once per match on the player's own machine and the real skin number is
/// what goes over the network, so everyone sees the same skin.
/// </summary>
internal static class RandomSkins
{
    private static readonly Dictionary<int, CustomSkin> _byType = new();
    private static readonly Dictionary<int, ESkinType> _pick = new();     // random type -> current pick
    private static readonly Dictionary<int, ESkinType> _last = new();     // avoid picking the same twice in a row
    private static readonly System.Random _rng = new();
    private static bool _registered;
    private static Sprite _portrait;

    public static WardrobeService Wardrobe;   // captured from patches, used for unlock checks

    public static bool IsRandom(ESkinType t) => _byType.ContainsKey((int)t);

    public static void EnsureRegistered()
    {
        if (_registered || !Plugin.Cfg.RandomSkin.Value) return;
        _registered = true;
        foreach (ECharacterType c in Enum.GetValues(typeof(ECharacterType)))
        {
            if (c == ECharacterType.None) continue;
            var skin = new CustomSkin
            {
                Id = "handsoverskins.random." + c.ToString().ToLowerInvariant(),
                DisplayName = "Random",
                Description = "A different look every match, picked from this character's skins (or only your starred favourites, see the config).",
                Author = "HandsOverSkins",
                Character = c,
                RequiredLevel = 1,
                Order = 100000,
                Portrait = Portrait(),
                Source = "HandsOverSkins (built-in)",
                IsRandom = true,
            };
            if (SkinAPI.RegisterBuiltin(skin)) _byType[(int)skin.SkinType] = skin;
        }
    }

    /// <summary>The real skin to use instead of a Random card (same answer until <see cref="Reroll"/>).</summary>
    public static ESkinType Resolve(ESkinType randomType)
    {
        if (!_byType.TryGetValue((int)randomType, out var r)) return randomType;
        if (_pick.TryGetValue((int)randomType, out var p)) return p;

        if (SkinInjector.LatestProvider == null) return ESkinType.Default; // too early: don't lock in a pick
        var pool = Pool(r.Character);
        ESkinType chosen = ESkinType.Default;
        if (pool.Count > 0)
        {
            if (pool.Count > 1 && _last.TryGetValue((int)randomType, out var prev)) pool.Remove(prev);
            chosen = pool[_rng.Next(pool.Count)];
        }
        _pick[(int)randomType] = chosen;
        _last[(int)randomType] = chosen;
        if (Plugin.Cfg.VerboseLogging.Value) Plugin.Logger.LogInfo($"Random skin for {r.Character}: #{(int)chosen}");
        return chosen;
    }

    public static void Reroll(ECharacterType? only = null)
    {
        if (only == null) { _pick.Clear(); return; }
        foreach (var kv in _byType) if (kv.Value.Character == only.Value) _pick.Remove(kv.Key);
    }

    private static List<ESkinType> Pool(ECharacterType character)
    {
        var result = new List<ESkinType>();
        var provider = SkinInjector.LatestProvider;
        if (provider == null) return result;
        Il2CppSystem.Collections.Generic.List<CharacterSkinConfig> list;
        if (!provider._skins.TryGetValue(character, out list) || list == null) return result;

        string mode = Plugin.Cfg.RandomPool.Value ?? "Unlocked";
        bool favs = mode.Equals("Favorites", StringComparison.OrdinalIgnoreCase);
        if (favs && !Favorites.Any(character)) mode = "Unlocked"; // nothing starred yet: behave like Unlocked
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (c == null || IsRandom(c.skinType)) continue;
            bool custom = SkinAPI.IsCustom(c.skinType);
            if (mode.Equals("CustomOnly", StringComparison.OrdinalIgnoreCase) && !custom) continue;
            if (mode.Equals("Favorites", StringComparison.OrdinalIgnoreCase) && !Favorites.Is(character, c.skinType)) continue;
            if (!mode.Equals("All", StringComparison.OrdinalIgnoreCase) && !Unlocked(c)) continue;
            if (!result.Contains(c.skinType)) result.Add(c.skinType);
        }
        return result;
    }

    private static bool Unlocked(CharacterSkinConfig c)
    {
        try { return Wardrobe == null || Wardrobe.IsUnlocked(c); }
        catch { return true; }
    }

    /// <summary>Wardrobe card: dark card with a big question mark (288×512 at 20 ppu, like the game's cards).</summary>
    private static Sprite Portrait()
    {
        if (_portrait != null && !_portrait.WasCollected) return _portrait;
        try
        {
            const int W = 288, H = 512;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false) { name = "HOS_RandomCard" };
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
            {
                float g = y / (float)H;
                var bg = new Color32((byte)(22 + 18 * g), (byte)(20 + 14 * g), (byte)(24 + 20 * g), 255);
                for (int x = 0; x < W; x++) px[y * W + x] = bg;
            }
            // 5x7 question mark, row 0 = top
            string[] glyph = { ".###.", "#...#", "....#", "...#.", "..#..", ".....", "..#.." };
            int cell = 34, gw = 5 * cell, gh = 7 * cell;
            int ox = (W - gw) / 2, oy = (H - gh) / 2;
            var ink = new Color32(225, 214, 196, 255);
            var shadow = new Color32(120, 20, 20, 255);
            void Block(int bx, int by, Color32 col, int shift)
            {
                for (int y = 0; y < cell - 4; y++)
                    for (int x = 0; x < cell - 4; x++)
                    {
                        int X = ox + bx * cell + x + shift, Y = H - 1 - (oy + by * cell + y) - shift;
                        if (X >= 0 && X < W && Y >= 0 && Y < H) px[Y * W + X] = col;
                    }
            }
            for (int r = 0; r < glyph.Length; r++)
                for (int c = 0; c < 5; c++)
                    if (glyph[r][c] == '#') Block(c, r, shadow, 5);
            for (int r = 0; r < glyph.Length; r++)
                for (int c = 0; c < 5; c++)
                    if (glyph[r][c] == '#') Block(c, r, ink, 0);
            tex.SetPixels32(px);
            tex.Apply();
            tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _portrait = Sprite.Create(tex, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), 20f);
            _portrait.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return _portrait;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Random card portrait: {e.Message}");
            return null;
        }
    }
}
