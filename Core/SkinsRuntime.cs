using System;
using UnityEngine;

namespace HandsOverSkins.Core;

/// <summary>Small per-frame helper (registered in IL2CPP via ClassInjector).</summary>
public class SkinsRuntime : MonoBehaviour
{
    public SkinsRuntime(IntPtr ptr) : base(ptr) { }

    private float _nextLocaleCheck;
    private string _locale;

    private static float _nextHideCheck;

    // after the animators: held-item offsets go on top of this frame's animated holder pose
    private void LateUpdate()
    {
        try { HeldItems.UpdateLocal(); }
        catch (Exception e) { Plugin.Logger.LogError($"Held items: {e}"); }
    }

    private void Update()
    {
        DescriptionGuard.Tick();
        try { LiveSkins.Tick(); }
        catch (Exception e) { Plugin.Logger.LogError($"Live skin change: {e}"); }
        try { FollowRigs.Scan(); }
        catch (Exception e) { Plugin.Logger.LogError($"Follow rig scan: {e}"); }
        if (Time.realtimeSinceStartup >= _nextHideCheck)
        {
            _nextHideCheck = Time.realtimeSinceStartup + 0.25f;
            try { HandsOverSkins.Patches.HiddenParts.ReapplyAll(); Stumps.UpdateAll(); MaterialStates.ReapplyAll(); }
            catch (Exception e) { Plugin.Logger.LogError($"Hidden parts: {e}"); }
        }

        if (Time.realtimeSinceStartup < _nextLocaleCheck) return;
        _nextLocaleCheck = Time.realtimeSinceStartup + 1f;
        // Don't touch Unity Localization before the game has built its providers (and initialized it).
        if (!SkinInjector.HasInjected || HandsOverSkins.API.SkinAPI.Skins.Count == 0) return;
        string code = LocalizationBridge.CurrentLocaleCode();
        if (code != _locale)
        {
            _locale = code;
            LocalizationBridge.EnsureEntries(force: true);
        }
    }
}
