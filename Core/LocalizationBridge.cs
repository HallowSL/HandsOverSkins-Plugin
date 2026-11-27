using System;
using System.Collections.Generic;
using HandsOverSkins.API;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;

namespace HandsOverSkins.Core;

/// <summary>
/// Skin names/descriptions.
///
/// The base game shows a skin's text through CharacterSkinConfig.description, a LocalizedString in the
/// "General" string table (e.g. WARDROBE_BEN_DESC_SKIN2). WardrobeCharactersView raises
/// DescriptionRequested(LocalizedString) on hover and WardrobeView.HandleDescriptionRequested writes it
/// to its descriptionText (TMP_Text).
///
/// For custom skins we:
///   1. give the config a LocalizedString pointing at a key we own (HOSKIN_&lt;number&gt;_DESC),
///   2. add that key to the active locale's string table (so the game's own lookup works), and
///   3. write the text directly from a Harmony postfix as a fallback, guarding it against a late async overwrite.
/// </summary>
internal static class LocalizationBridge
{
    private static readonly Dictionary<IntPtr, CustomSkin> _byDescription = new();
    private static readonly List<LocalizedString> _keepAlive = new();
    private static string _filledForLocale;
    private static bool _tableWarned;

    public static LocalizedString CreateDescription(CustomSkin skin)
    {
        LocalizedString ls;
        try
        {
            TableReference table = Plugin.Cfg.LocalizationTable.Value;
            TableEntryReference entry = skin.LocalizationKey;
            ls = new LocalizedString(table, entry);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Could not build LocalizedString for {skin.Id} ({e.Message}); using direct text only.");
            ls = new LocalizedString();
        }
        _byDescription[ls.Pointer] = skin;
        _keepAlive.Add(ls);
        _filledForLocale = null; // new key -> refill
        return ls;
    }

    public static bool TryGetSkin(LocalizedString description, out CustomSkin skin)
    {
        skin = null;
        if (description == null) return false;
        if (_byDescription.TryGetValue(description.Pointer, out skin)) return true;
        try
        {
            string key = description.TableEntryReference.Key;
            if (!string.IsNullOrEmpty(key) && key.StartsWith("HOSKIN_", StringComparison.Ordinal))
                foreach (var s in SkinAPI.Skins)
                    if (s.LocalizationKey == key) { skin = s; return true; }
        }
        catch { /* ignored */ }
        return false;
    }

    public static string CurrentLocaleCode()
    {
        try
        {
            var l = LocalizationSettings.SelectedLocale;
            return l != null ? l.Identifier.Code : "en";
        }
        catch { return "en"; }
    }

    /// <summary>Make sure every custom key exists in the selected locale's table.</summary>
    public static void EnsureEntries(bool force = false)
    {
        string code = CurrentLocaleCode();
        if (!force && code == _filledForLocale) return;
        try
        {
            var locale = LocalizationSettings.SelectedLocale;
            TableReference tableRef = Plugin.Cfg.LocalizationTable.Value;
            StringTable table = LocalizationSettings.StringDatabase.GetTable(tableRef, locale);
            if (table == null)
            {
                if (!_tableWarned) Plugin.Logger.LogWarning($"String table '{Plugin.Cfg.LocalizationTable.Value}' not found for '{code}'.");
                _tableWarned = true;
                return;
            }
            foreach (var skin in SkinAPI.Skins)
            {
                if (skin.Config == null) continue;
                string text = skin.FormatDescription(code);
                var entry = table.GetEntry(skin.LocalizationKey);
                if (entry == null) table.AddEntry(skin.LocalizationKey, text);
                else entry.Value = text;
            }
            _filledForLocale = code;
        }
        catch (Exception e)
        {
            if (!_tableWarned) Plugin.Logger.LogWarning($"Could not add custom skin strings to the localization table: {e.Message}. Falling back to direct text.");
            _tableWarned = true;
        }
    }
}

/// <summary>
/// Keeps the wardrobe description showing our text for a short moment after it was set,
/// in case the game's async LocalizedString lookup completes afterwards and overwrites it.
/// </summary>
internal static class DescriptionGuard
{
    private static TMP_Text _target;
    private static string _text;
    private static float _until;

    public static void Set(TMP_Text target, string text)
    {
        _target = target;
        _text = text;
        _until = Time.realtimeSinceStartup + 1.5f;
        if (target != null) target.text = text;
    }

    public static void Clear() { _target = null; _text = null; }

    public static void Tick()
    {
        if (_target == null || _text == null) return;
        if (Time.realtimeSinceStartup > _until || _target.WasCollected) { Clear(); return; }
        if (_target.text != _text) _target.text = _text;
    }
}
