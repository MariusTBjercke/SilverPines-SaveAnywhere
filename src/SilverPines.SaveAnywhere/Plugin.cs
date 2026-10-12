using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace SaveAnywhere;

[BepInPlugin(Guid, "Silver Pines - Save Anywhere", Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "com.mariustbjercke.silverpines.saveanywhere";
    public const string Version = "1.0.0";

    // Calm PlayerFSM states: standing, walking, sprinting, turning and crouching.
    private const string DefaultAllowedStates =
        "Idle, Walk, WalkStart, WalkInAnimTime, Sprint, SprintStart, SprintEnd, Turn, Turn Run, CrouchFSM";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> FreePhoneCalls;
    internal static ConfigEntry<bool> SaveAnywhereEnabled;
    internal static ConfigEntry<KeyCode> SaveAnywhereKey;
    internal static ConfigEntry<bool> SkipPhoneCallAfterHotkeySave;
    internal static ConfigEntry<string> AllowedPlayerStates;

    public override void Load()
    {
        Logger = Log;

        FreePhoneCalls = Config.Bind("General", "FreePhoneCalls", false,
            "Opt-in: payphones never need a coin. This makes ALL calls free, not just the save number.");
        SaveAnywhereEnabled = Config.Bind("General", "SaveAnywhereEnabled", true,
            "Open the save menu anywhere with the hotkey below.");
        SaveAnywhereKey = Config.Bind("General", "SaveAnywhereKey", KeyCode.F5,
            "Hotkey that opens the save menu.");
        SkipPhoneCallAfterHotkeySave = Config.Bind("General", "SkipPhoneCallAfterHotkeySave", true,
            "When loading a save made with the hotkey, skip the phone conversation that normally plays on load.");
        AllowedPlayerStates = Config.Bind("Safety", "AllowedPlayerStates", DefaultAllowedStates,
            "The hotkey only works while the player's state machine (PlayerFSM) is in one of these states, so you " +
            "can't save mid-jump, on a ladder, while grabbed, dying, etc. Comma-separated. Leave empty to allow any state.");

        new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);

        ClassInjector.RegisterTypeInIl2Cpp<SaveHotkeyBehaviour>();
        AddComponent<SaveHotkeyBehaviour>();

        Log.LogInfo($"Loaded. FreePhoneCalls={FreePhoneCalls.Value}, SaveAnywhere={SaveAnywhereEnabled.Value} ({SaveAnywhereKey.Value}), SkipPhoneCallAfterHotkeySave={SkipPhoneCallAfterHotkeySave.Value}");
    }
}

/// <summary>
/// PhoneMinigame.Setup(isPayphone, data) sets RequiresCoin = isPayphone, and Call() refuses to dial
/// while RequiresCoin is true. Clearing it right after Setup lets you dial the save number for free;
/// TryAddCoin() only takes money while RequiresCoin is true, so no coins are spent.
/// </summary>
[HarmonyPatch(typeof(PhoneMinigame), nameof(PhoneMinigame.Setup))]
internal static class PhoneMinigameSetupPatch
{
    private static void Postfix(PhoneMinigame __instance, bool isPayphone)
    {
        if (!Plugin.FreePhoneCalls.Value || !__instance.RequiresCoin) return;
        __instance.RequiresCoin = false;
        Plugin.Logger.LogInfo($"Phone set up (payphone={isPayphone}): coin requirement removed.");
    }
}

/// <summary>
/// Opens the game's own (unused) SaveDuringGameMenu, which raises the same save event as the phone.
/// </summary>
public class SaveHotkeyBehaviour : MonoBehaviour
{
    public SaveHotkeyBehaviour(IntPtr ptr) : base(ptr) { }

    private void Update()
    {
        HotkeySaveLoadWatcher.Tick();

        if (!Plugin.SaveAnywhereEnabled.Value) return;

        bool pressed;
        try { pressed = UnityInput.Current.GetKeyDown(Plugin.SaveAnywhereKey.Value); }
        catch { return; }
        if (!pressed) return;

        try { TryOpenSaveMenu(); }
        catch (Exception e) { Plugin.Logger.LogError($"Save hotkey failed: {e}"); }
    }

    private static void TryOpenSaveMenu()
    {
        var menuState = FindFirst<GameMenuState>();
        if (menuState != null && menuState.IsInAnyMenu())
        {
            Plugin.Logger.LogInfo($"Save hotkey ignored: a menu is open ({menuState.ActiveMenuState}).");
            return;
        }

        if (!IsPlayerCalm(out var reason))
        {
            Plugin.Logger.LogInfo($"Save hotkey ignored: {reason}.");
            return;
        }

        var saveMenu = FindFirst<SaveDuringGameMenu>();
        if (saveMenu == null)
        {
            Plugin.Logger.LogWarning("Save hotkey: no SaveDuringGameMenu exists right now (are you in-game?).");
            return;
        }

        Plugin.Logger.LogInfo($"Opening save menu ({saveMenu.gameObject.name}).");
        HotkeySaveMarker.Apply();
        saveMenu.Show();
    }

    private static bool IsPlayerCalm(out string reason)
    {
        var health = GlobalReferences.Instance?.Variables?.Generic?.PlayerHealth;
        if (health != null && health.Value <= 0)
        {
            reason = "the player has no health left";
            return false;
        }

        var allowed = Plugin.AllowedPlayerStates.Value;
        if (string.IsNullOrWhiteSpace(allowed)) { reason = null; return true; }

        var player = FindPlayerFsm();
        if (player == null)
        {
            reason = "PlayerFSM not found (are you in-game?)";
            return false;
        }

        var state = player.ActiveStateName;
        foreach (var s in allowed.Split(','))
        {
            if (s.Trim() == state) { reason = null; return true; }
        }
        reason = $"player state '{state}' is not in AllowedPlayerStates";
        return false;
    }

    internal static PlayMakerFSM FindPlayerFsm()
    {
        var list = PlayMakerFSM.FsmList;
        if (list == null) return null;
        for (int i = 0; i < list.Count; i++)
        {
            var fsm = list[i];
            if (fsm != null && fsm.FsmName == "PlayerFSM") return fsm;
        }
        return null;
    }

    // Includes inactive objects, since the menu sits disabled until shown.
    internal static T FindFirst<T>() where T : UnityEngine.Object
    {
        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>());
        foreach (var o in all)
        {
            var t = o.TryCast<T>();
            if (t == null) continue;
            // Skip prefab assets that aren't part of a loaded scene.
            var c = t.TryCast<Component>();
            if (c != null && !c.gameObject.scene.isLoaded) continue;
            return t;
        }
        return null;
    }
}

/// <summary>
/// On load, the PlayMaker action LoadGameContinuePhoneCall replays the phone conversation named by
/// PhoneMemory.m_savePhoneEvent (written into the save by the payphone). For hotkey saves we put a
/// marker name there instead, so the save itself remembers it wasn't made at a phone. The marker
/// matches no real phone event, and the previous value is restored when the save menu closes.
/// </summary>
internal static class HotkeySaveMarker
{
    internal const string Marker = "__SaveAnywhere_HotkeySave__";

    private static PhoneMemory s_memory;
    private static string s_previous;
    private static bool s_applied;

    internal static bool IsApplied => s_applied;

    internal static void Apply()
    {
        if (!Plugin.SkipPhoneCallAfterHotkeySave.Value) return;
        var phoneBook = SaveHotkeyBehaviour.FindFirst<PhoneBook>();
        var memory = phoneBook != null ? phoneBook.m_phoneMemory : null;
        if (memory == null)
        {
            Plugin.Logger.LogWarning("No PhoneMemory found; this save will play the phone call on load.");
            return;
        }
        if (!s_applied)
        {
            s_memory = memory;
            s_previous = memory.m_savePhoneEvent;
            s_applied = true;
        }
        memory.m_savePhoneEvent = Marker;
        Plugin.Logger.LogInfo($"Marked as hotkey save (previous savePhoneEvent='{s_previous}', PhoneMemory objects loaded: {Resources.FindObjectsOfTypeAll(Il2CppType.Of<PhoneMemory>()).Length}).");
    }

    internal static void Restore()
    {
        if (!s_applied) return;
        s_applied = false;
        if (s_memory != null && s_memory.m_savePhoneEvent == Marker)
            s_memory.m_savePhoneEvent = s_previous;
        s_memory = null;
        s_previous = null;
    }
}

[HarmonyPatch(typeof(SaveDuringGameMenu), nameof(SaveDuringGameMenu.Hide))]
internal static class SaveDuringGameMenuHidePatch
{
    private static void Postfix(SaveDuringGameMenu __instance)
    {
        try { HotkeySaveMarker.Restore(); }
        catch (Exception e) { Plugin.Logger.LogError($"Restoring phone memory failed: {e}"); }

        // DoSaveGameFlow resets m_selectedSaveIndex to -1 and only sets it when a slot is picked,
        // so >= 0 here means a save went through. Mirror the payphone, which sets PV_PlayerHasSaved.
        try
        {
            if (__instance.m_selectedSaveIndex < 0) return;
            var hasSaved = GlobalReferences.Instance?.Variables?.Mechanics?.PV_PlayerHasSaved;
            if (hasSaved != null && !hasSaved.Value)
            {
                hasSaved.SetValue(true);
                Plugin.Logger.LogInfo("Set PV_PlayerHasSaved = true (as the payphone does).");
            }
        }
        catch (Exception e) { Plugin.Logger.LogError($"Setting PV_PlayerHasSaved failed: {e}"); }
    }
}

[HarmonyPatch(typeof(Actions.LoadGameContinuePhoneCall), nameof(Actions.LoadGameContinuePhoneCall.OnEnter))]
internal static class LoadGameContinuePhoneCallPatch
{
    private static bool Prefix(Actions.LoadGameContinuePhoneCall __instance)
    {
        try
        {
            var phoneBook = __instance.m_phoneBook;
            var memory = phoneBook != null ? phoneBook.m_phoneMemory : null;
            var savedEvent = memory != null ? memory.m_savePhoneEvent : null;
            Plugin.Logger.LogInfo($"Load phone call: owner={__instance.Owner?.name}, savePhoneEvent='{savedEvent}'");

            if (savedEvent != HotkeySaveMarker.Marker) return true;

            // Leave PhoneMemory as "no save call yet", the same state as before any payphone save.
            memory.m_savePhoneEvent = null;
            // PlayerFSM: CutsceneUsePhone -> LoadGamePhoneCall -FINISHED-> UsePhoneExit (hang-up anim) -Reset-> Idle.
            // The global "Reset" event goes straight to Idle, skipping both the call and the hang-up.
            Plugin.Logger.LogInfo("Hotkey save: skipping the phone conversation and hang-up animation.");
            __instance.Fsm.Event("Reset");
            return false;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Load phone call patch failed, falling back to the normal call: {e}");
            return true;
        }
    }
}

/// <summary>
/// After loading a save, PlayerFSM idles in 'CutsceneUsePhone' (phone to the ear) until the game sends
/// GameStart, which is why the main-menu background shows the phone pose. For hotkey saves, send the
/// global 'Reset' event (-> Idle) as soon as we see that state, so the pose never shows. The
/// LoadGameContinuePhoneCall patch stays as a fallback in case this misses.
/// </summary>
internal static class HotkeySaveLoadWatcher
{
    private const string PhoneState = "CutsceneUsePhone";

    private static PhoneMemory s_memory;
    private static float s_nextMemoryLookup;

    internal static void Tick()
    {
        if (!Plugin.SkipPhoneCallAfterHotkeySave.Value || HotkeySaveMarker.IsApplied) return;
        try
        {
            var memory = GetMemory();
            if (memory == null || memory.m_savePhoneEvent != HotkeySaveMarker.Marker) return;

            var player = SaveHotkeyBehaviour.FindPlayerFsm();
            if (player == null || player.ActiveStateName != PhoneState) return;

            memory.m_savePhoneEvent = null;
            player.SendEvent("Reset");
            Plugin.Logger.LogInfo("Hotkey save loaded: left the phone pose before the game started.");
        }
        catch (Exception e)
        {
            s_memory = null;
            Plugin.Logger.LogError($"Hotkey save load watcher failed: {e}");
        }
    }

    private static PhoneMemory GetMemory()
    {
        if (s_memory != null && !s_memory.WasCollected) return s_memory;
        s_memory = null;
        if (Time.realtimeSinceStartup < s_nextMemoryLookup) return null;
        s_nextMemoryLookup = Time.realtimeSinceStartup + 1f;
        var phoneBook = SaveHotkeyBehaviour.FindFirst<PhoneBook>();
        s_memory = phoneBook != null ? phoneBook.m_phoneMemory : null;
        return s_memory;
    }
}
