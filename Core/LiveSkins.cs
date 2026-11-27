using System;
using System.Collections.Generic;
using HandsOverSkins.API;
using Jaws.Characters;
using Jaws.Chat;
using Jaws.Gameplay.Character;
using Jaws.Gameplay.Character.Visual;
using UnityEngine;
using UnityEngine.InputSystem;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace HandsOverSkins.Core;

/// <summary>
/// Changing costume in the middle of a match.
///
/// Hotkeys (config "Live Change") cycle the local player's skin. The change is applied to both of the character's
/// bodies (alive and corpse) right away and announced to the other players through the game's own chat RPC as a
/// short marker line ("HOSKIN &lt;clientId&gt; &lt;skin&gt;"). Players running the mod apply it and hide the line; players
/// without it see the line in chat but nothing else changes for them. A skin the receiver doesn't have installed
/// falls back to the character's default, same as at match start.
///
/// The override lasts as long as that match's CharacterVisualController (a new match starts from the wardrobe pick).
/// </summary>
internal static class LiveSkins
{
    internal const string Marker = "HOSKIN ";

    private static readonly Dictionary<IntPtr, CharacterVisualController> _visuals = new();
    private static readonly Dictionary<IntPtr, ESkinType> _override = new();   // logic -> forced skin
    private static readonly Dictionary<IntPtr, ESkinType> _current = new();    // logic -> last applied skin
    // last ShowHead / ShowFullBody state per body: a new skin comes in fully visible, so these are replayed after a
    // live change (first-person hides your own head)
    private static readonly Dictionary<IntPtr, bool> _head = new(), _fullBody = new();

    public static void NoteShowHead(CharacterSkinLogic logic, bool state) { if (logic != null) _head[logic.Pointer] = state; }
    public static void NoteShowFullBody(CharacterSkinLogic logic, bool state) { if (logic != null) _fullBody[logic.Pointer] = state; }
    private static Key _next = Key.None, _prev = Key.None, _rand = Key.None, _fav = Key.None;
    private static string _keysFrom;
    private static readonly System.Random _rng = new();

    public static void Register(CharacterVisualController cvc)
    {
        if (cvc == null) return;
        _visuals[cvc.Pointer] = cvc;
    }

    /// <summary>TrySetSkin postfix: remember what each body is wearing.</summary>
    public static void Note(CharacterSkinLogic logic, ESkinType skin)
    {
        if (logic != null) _current[logic.Pointer] = skin;
    }

    /// <summary>TrySetSkin prefix: a body with a live override keeps it if the game re-dresses it later in the match.</summary>
    public static bool TryOverride(CharacterSkinLogic logic, out ESkinType skin)
    {
        skin = default;
        return logic != null && _override.TryGetValue(logic.Pointer, out skin);
    }

    public static void Forget(CharacterSkinLogic logic)
    {
        if (logic == null) return;
        _override.Remove(logic.Pointer);
        _current.Remove(logic.Pointer);
        _head.Remove(logic.Pointer);
        _fullBody.Remove(logic.Pointer);
    }

    // ------------------------------------------------------------------ input

    public static void Tick()
    {
        if (!Plugin.Cfg.LiveChange.Value) return;
        var kb = Keyboard.current;
        if (kb == null) return;
        ReadKeys();
        int dir = 0; bool random = false, favOnly = false;
        if (Pressed(kb, _next)) dir = 1;
        else if (Pressed(kb, _prev)) dir = -1;
        else if (Pressed(kb, _rand)) random = true;
        else if (Pressed(kb, _fav)) { random = true; favOnly = true; }
        else return;

        if (ChatOpen()) return;
        var cvc = LocalVisual(out var manager);
        if (cvc == null) return;
        var logic = cvc._aliveSkinLogic;
        if (logic == null) return;
        var character = manager.characterType;

        var pool = Pool(character, favOnly);
        if (pool.Count == 0) return;
        _current.TryGetValue(logic.Pointer, out var cur);
        int at = pool.IndexOf(cur);
        ESkinType pick;
        if (random)
        {
            if (pool.Count > 1 && at >= 0) pool.RemoveAt(at);
            pick = pool[_rng.Next(pool.Count)];
        }
        else pick = pool[((at < 0 ? (dir > 0 ? -1 : 0) : at) + dir + pool.Count) % pool.Count];

        Apply(cvc, pick);
        Broadcast(manager.ClientId, pick);
        ShowToast(pick, character);
    }

    private static bool Pressed(Keyboard kb, Key k)
    {
        if (k == Key.None) return false;
        try { return kb[k].wasPressedThisFrame; } catch { return false; }
    }

    private static void ReadKeys()
    {
        string sig = Plugin.Cfg.LiveNextKey.Value + "|" + Plugin.Cfg.LivePrevKey.Value + "|" + Plugin.Cfg.LiveRandomKey.Value + "|" + Plugin.Cfg.LiveFavoriteKey.Value;
        if (sig == _keysFrom) return;
        _keysFrom = sig;
        _next = Parse(Plugin.Cfg.LiveNextKey.Value);
        _prev = Parse(Plugin.Cfg.LivePrevKey.Value);
        _rand = Parse(Plugin.Cfg.LiveRandomKey.Value);
        _fav = Parse(Plugin.Cfg.LiveFavoriteKey.Value);
    }

    private static Key Parse(string s) =>
        Enum.TryParse<Key>((s ?? "").Trim(), true, out var k) ? k : Key.None;

    private static bool ChatOpen()
    {
        try
        {
            var chat = First<ChatContainerView>();
            return chat != null && chat.IsChatMode;
        }
        catch { return false; }
    }

    private static CharacterVisualController LocalVisual(out CharacterManager manager)
    {
        manager = null;
        foreach (var m in All<CharacterManager>())
        {
            if (m == null || !m.IsLocalOwner) continue;
            manager = m;
            return m.characterVisualController;
        }
        return null;
    }

    /// <summary>The character's real skins the player may wear (unlocked ones, or the favourites).</summary>
    private static List<ESkinType> Pool(ECharacterType character, bool favOnly)
    {
        var result = new List<ESkinType>();
        var provider = SkinInjector.LatestProvider;
        if (provider == null) return result;
        Il2CppSystem.Collections.Generic.List<CharacterSkinConfig> list;
        if (!provider._skins.TryGetValue(character, out list) || list == null) return result;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (c == null || RandomSkins.IsRandom(c.skinType)) continue;
            if (favOnly && !Favorites.Is(character, c.skinType)) continue;
            bool unlocked = true;
            try { unlocked = RandomSkins.Wardrobe == null || RandomSkins.Wardrobe.IsUnlocked(c); } catch { }
            if (!unlocked) continue;
            if (!result.Contains(c.skinType)) result.Add(c.skinType);
        }
        return result;
    }

    private static List<T> All<T>() where T : Il2CppObjectBase
    {
        var list = new List<T>();
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<T>(), FindObjectsSortMode.None))
        {
            var t = o?.TryCast<T>();
            if (t != null) list.Add(t);
        }
        return list;
    }

    private static T First<T>() where T : Il2CppObjectBase
    {
        var all = All<T>();
        return all.Count > 0 ? all[0] : null;
    }

    // ------------------------------------------------------------------ apply / sync

    /// <summary>Dress both bodies of a character in <paramref name="skin"/>, keeping cut hands cut.</summary>
    public static void Apply(CharacterVisualController cvc, ESkinType skin)
    {
        if (cvc == null) return;
        foreach (var logic in new[] { cvc._aliveSkinLogic, cvc._deathSkinLogic })
        {
            if (logic == null) continue;
            var cut = Stumps.CutSides(logic);
            _override[logic.Pointer] = skin;
            try { logic.TrySetSkin(skin); }
            catch (Exception e) { Plugin.Logger.LogError($"Live skin change: {e}"); continue; }
            try
            {
                if (_fullBody.TryGetValue(logic.Pointer, out bool full)) logic.ShowFullBody(full);
                if (_head.TryGetValue(logic.Pointer, out bool head)) logic.ShowHead(head);
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"Live skin change (head/body visibility): {e.Message}"); }
            try
            {
                if (cut.left) logic.CutHand(false);
                if (cut.right) logic.CutHand(true);
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"Live skin change (re-cut hands): {e.Message}"); }
        }
        try { HandsOverSkins.Patches.HiddenParts.ReapplyAll(); Stumps.UpdateAll(); MaterialStates.ReapplyAll(); } catch { }
    }

    private static void Broadcast(ulong clientId, ESkinType skin)
    {
        if (!Plugin.Cfg.LiveSync.Value) return;
        try
        {
            var chat = First<ChatManager>();
            if (chat == null) return;
            string msg = $"{Marker}{clientId} {(int)skin}";
            chat.SendMessage_Rpc(new Unity.Collections.FixedString512Bytes(msg), new Unity.Netcode.RpcParams());
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"Live skin sync: {e.Message}"); }
    }

    /// <summary>ChatContainerView.AddPlayerMessage prefix: true if the line was one of ours (then it's not shown).</summary>
    public static bool TryHandleChat(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int at = text.IndexOf(Marker, StringComparison.Ordinal);
        if (at < 0) return false;
        var parts = text.Substring(at + Marker.Length).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !ulong.TryParse(parts[0], out ulong clientId) || !int.TryParse(parts[1], out int skinNum))
            return false;
        try
        {
            foreach (var m in All<CharacterManager>())
            {
                if (m == null || m.ClientId != clientId) continue;
                if (m.IsLocalOwner) break; // already applied when we pressed the key
                Apply(m.characterVisualController, (ESkinType)skinNum);
                break;
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"Live skin sync (receive): {e.Message}"); }
        return true;
    }

    private static void ShowToast(ESkinType skin, ECharacterType character)
    {
        string name = SkinAPI.TryGetSkin(skin, out var cs) ? cs.DisplayName : skin.ToString();
        Plugin.Logger.LogInfo($"Live skin change: {character} -> {name}");
    }
}
