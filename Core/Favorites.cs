using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using HandsOverSkins.API;
using Il2CppInterop.Runtime;
using Jaws.Characters;
using Jaws.MainMenu.UI.Wardrobe;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace HandsOverSkins.Core;

/// <summary>
/// Favourite skins. Every skin card in the wardrobe gets a star in its corner; starred skins are pinned to the
/// front of that character's skin page, can be the only pool of the Random card (Random Skin.Pool = Favorites)
/// and of the "random favourite" live-change hotkey. Saved to BepInEx/config/HandsOverSkins.favorites.txt
/// (one key per line: the custom skin id, or "Character:number" for the game's own skins).
/// </summary>
internal static class Favorites
{
    private static HashSet<string> _set;
    private static string FilePath => Path.Combine(Paths.ConfigPath, "HandsOverSkins.favorites.txt");
    private static Sprite _starOn, _starOff;
    private static WardrobeCharactersView _view;

    private static HashSet<string> Set
    {
        get
        {
            if (_set != null) return _set;
            _set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(FilePath))
                    foreach (var l in File.ReadAllLines(FilePath))
                        if (!string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#")) _set.Add(l.Trim());
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"Favorites: {e.Message}"); }
            return _set;
        }
    }

    private static void Save()
    {
        try
        {
            File.WriteAllLines(FilePath, new[] { "# HandsOverSkins favourite skins (one per line)" }.Concat(Set.OrderBy(s => s)));
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"Favorites (save): {e.Message}"); }
    }

    public static string Key(ECharacterType character, ESkinType skin) =>
        SkinAPI.TryGetSkin(skin, out var cs) ? cs.Id : $"{character}:{(int)skin}";

    public static bool Is(ECharacterType character, ESkinType skin) => Set.Contains(Key(character, skin));

    public static bool Toggle(ECharacterType character, ESkinType skin)
    {
        string k = Key(character, skin);
        bool now = !Set.Remove(k);
        if (now) Set.Add(k);
        Save();
        return now;
    }

    public static bool Any(ECharacterType character)
    {
        var provider = SkinInjector.LatestProvider;
        if (provider == null) return false;
        Il2CppSystem.Collections.Generic.List<CharacterSkinConfig> list;
        if (!provider._skins.TryGetValue(character, out list) || list == null) return false;
        for (int i = 0; i < list.Count; i++)
            if (list[i] != null && !RandomSkins.IsRandom(list[i].skinType) && Is(character, list[i].skinType)) return true;
        return false;
    }

    // ------------------------------------------------------------------ wardrobe UI

    /// <summary>WardrobeCharactersView.BuildSkinCards postfix.</summary>
    public static void Decorate(WardrobeCharactersView view)
    {
        if (!Plugin.Cfg.FavoritesEnabled.Value || view == null) return;
        _view = view;
        var cards = view._skinCards;
        if (cards == null) return;
        foreach (var kv in cards)
        {
            var card = kv.Key;
            var cfg = kv.Value;
            if (card == null || cfg == null || RandomSkins.IsRandom(cfg.skinType)) continue;
            AddStar(card, cfg);
        }
        Sort(view);
    }

    private static void AddStar(WardrobeCardView card, CharacterSkinConfig cfg)
    {
        var existing = card.transform.Find("HOS_Favorite");
        GameObject go;
        if (existing != null) go = existing.gameObject;
        else
        {
            go = new GameObject("HOS_Favorite");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(card.transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            float size = Mathf.Clamp(Plugin.Cfg.FavoriteStarSize.Value, 16f, 160f);
            rt.sizeDelta = new Vector2(size, size);
            rt.anchoredPosition = new Vector2(-size * 0.15f, -size * 0.15f);
            go.AddComponent<Image>();
            go.AddComponent<Button>();
        }
        go.transform.SetAsLastSibling();
        var img = go.GetComponent<Image>();
        var btn = go.GetComponent<Button>();
        var character = cfg.characterType;
        var skin = cfg.skinType;
        Paint(img, Is(character, skin));
        btn.onClick.RemoveAllListeners();
        btn.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new Action(() =>
        {
            bool on = Toggle(character, skin);
            Paint(img, on);
            if (_view != null && !_view.WasCollected) Sort(_view);
        })));
    }

    private static void Paint(Image img, bool on)
    {
        if (img == null) return;
        img.sprite = on ? StarOn() : StarOff();
        img.color = on ? Color.white : new Color(1f, 1f, 1f, 0.55f);
        img.preserveAspect = true;
    }

    /// <summary>Favourites first (keeping the game's order inside each group).</summary>
    private static void Sort(WardrobeCharactersView view)
    {
        if (!Plugin.Cfg.FavoritesFirst.Value) return;
        var cards = view._skinCards;
        if (cards == null || cards.Count == 0) return;
        var all = new List<(Transform t, int idx, int group)>();
        Transform parent = null;
        foreach (var kv in cards)
        {
            var card = kv.Key;
            if (card == null || kv.Value == null) continue;
            var t = card.transform;
            parent ??= t.parent;
            if (t.parent != parent) continue;
            int group = RandomSkins.IsRandom(kv.Value.skinType) ? (Plugin.Cfg.RandomCardFirst.Value ? 0 : 3)
                      : Is(kv.Value.characterType, kv.Value.skinType) ? 1 : 2;
            all.Add((t, OriginalIndex(t), group));
        }
        if (parent == null) return;
        int start = all.Count == 0 ? 0 : all.Min(a => a.t.GetSiblingIndex());
        int i = start;
        foreach (var a in all.OrderBy(a => a.group).ThenBy(a => a.idx))
            a.t.SetSiblingIndex(i++);
    }

    // the game's order, remembered the first time we see a card (cards are rebuilt per page open)
    private static readonly Dictionary<IntPtr, int> _orig = new();
    private static int OriginalIndex(Transform t)
    {
        if (!_orig.TryGetValue(t.Pointer, out int i)) _orig[t.Pointer] = i = t.GetSiblingIndex();
        return i;
    }

    public static void ForgetCards() => _orig.Clear();

    // ------------------------------------------------------------------ star sprites

    private static Sprite StarOn() => _starOn != null && !_starOn.WasCollected ? _starOn
        : _starOn = MakeStar(new Color32(255, 205, 64, 255), new Color32(90, 50, 0, 255), true);
    private static Sprite StarOff() => _starOff != null && !_starOff.WasCollected ? _starOff
        : _starOff = MakeStar(new Color32(235, 228, 210, 255), new Color32(20, 18, 16, 200), false);

    /// <summary>Five-point star, anti-aliased, with a dark rim so it reads on any card art.</summary>
    private static Sprite MakeStar(Color32 fill, Color32 rim, bool filled)
    {
        const int N = 96;
        var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { name = "HOS_Star", wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[N * N];
        var outer = Star(N * 0.5f, N * 0.5f, N * 0.47f, N * 0.20f);
        var inner = Star(N * 0.5f, N * 0.5f, N * 0.36f, N * 0.15f);
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float cov = 0, covIn = 0;
                for (int sy = 0; sy < 3; sy++)
                    for (int sx = 0; sx < 3; sx++)
                    {
                        var p = new Vector2(x + (sx + 0.5f) / 3f, y + (sy + 0.5f) / 3f);
                        if (Inside(outer, p)) cov++;
                        if (Inside(inner, p)) covIn++;
                    }
                cov /= 9f; covIn /= 9f;
                Color c = Color.clear;
                if (cov > 0)
                {
                    Color r = rim;
                    Color f = filled ? (Color)fill : new Color(fill.r / 255f, fill.g / 255f, fill.b / 255f, 0.15f);
                    c = Color.Lerp(r, f, covIn);
                    c.a *= cov;
                }
                px[y * N + x] = c;
            }
        tex.SetPixels32(px);
        tex.Apply();
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var s = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
        s.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return s;
    }

    private static Vector2[] Star(float cx, float cy, float ro, float ri)
    {
        var pts = new Vector2[10];
        for (int i = 0; i < 10; i++)
        {
            float a = Mathf.PI / 2f + i * Mathf.PI / 5f;
            float r = (i & 1) == 0 ? ro : ri;
            pts[i] = new Vector2(cx + r * Mathf.Cos(a), cy + r * Mathf.Sin(a));
        }
        return pts;
    }

    private static bool Inside(Vector2[] poly, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].y > p.y) != (poly[j].y > p.y) &&
                p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                inside = !inside;
        return inside;
    }
}
