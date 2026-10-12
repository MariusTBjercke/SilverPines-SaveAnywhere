# AGENTS.md

Notes for AI coding agents (and humans) working on this repo. The README is for players; this file covers how the mod works internally and how to investigate the game.

## Project at a glance

- BepInEx 6 **IL2CPP** plugin for *Silver Pines* (Steam app 2333000, Unity 6000.3.15f1, IL2CPP metadata v39). The exe is `Carpenter.exe`, and the game code is mostly in `CarpenterScripts` (namespace-less) plus PlayMaker FSMs stored in scene assets.
- All plugin code is in `src/SilverPines.SaveAnywhere/Plugin.cs`. GUID `com.mariustbjercke.silverpines.saveanywhere`. The version lives in **two places** and must match: `<Version>` in the csproj and `Plugin.Version`.
- Install path: `BepInEx/plugins/SilverPines.SaveAnywhere/`. Config: `BepInEx/config/com.mariustbjercke.silverpines.saveanywhere.cfg`.
- Owner's machine: the game is at `D:\SteamLibrary\steamapps\common\Silver Pines`. That path is set in the git-ignored `Local.props` (`GameDir`). Saves are in `%USERPROFILE%\AppData\LocalLow\Wych Elm\Silver Pines\<steamid>\`.

## Build, deploy, release

```powershell
dotnet build -c Release                  # builds and copies the DLL into the game's plugins folder
dotnet build -c Release -p:DeployToGame=false
.\scripts\package.ps1                    # dist\*.zip: plugin-only and with-BepInEx (be.788)
gh release create vX.Y.Z --draft --title "Save Anywhere vX.Y.Z" --notes-file <notes.md> dist\*.zip
```

- The build references `BepInEx\core` and the generated `BepInEx\interop` DLLs in the game folder. CI can't build this repo, because the interop DLLs are generated from the game and must not be committed.
- `package.ps1` writes zip entries with `/` on purpose. Windows PowerShell 5.1's `CreateFromDirectory` writes `\`, which breaks extraction on Linux/Steam Deck.
- There are no automated tests. Verify in-game and read `BepInEx\LogOutput.log` (the plugin logs every decision it makes). The log is overwritten on each launch.
- Release flow so far: build → owner tests in-game → draft release → owner publishes. Bump the version in both places and update `CHANGELOG.md`.

## How the game works (findings)

### Payphone coins (`PhoneMinigame`)
- `Setup(bool isPayphone, PhoneMinigameData)` sets `RequiresCoin = isPayphone`.
- `Call()`, `AddInput()`, `PressedButton()` and the others do nothing while `m_requiresCoin` is true. Input and dialing are blocked until a coin is inserted.
- `TryAddCoin()`: if `m_requiresCoin` is true and the player has money, it calls `PlayerMainInventory.RemoveMoney(1)`, then `RequiresCoin = false` and `m_insertedCoin = true`. Coins are just the player's money.
- `FreePhoneCalls` = a Postfix on `Setup` that sets `RequiresCoin = false`. This makes *all* numbers free, because the gate isn't per-number.
- Difficulty flag `GameDifficultyFlags.AllowMagicPhoneCoin` (4) plus `CanGenerateMagicCoin()` gives a free coin on easy difficulties when you're nearly broke.

### Saving
- `SaveDuringGameMenu` is a complete save-slot screen that no game code opens. `Show()` sets the menu state and shows the `SaveSelectPanel`. `DoSaveGameFlow` resets `m_selectedSaveIndex = -1`, waits for a slot, then raises the same save `BaseGameEventChannel<int>` as the payphone, waits for completion, and calls `Hide()`.
- The payphone flow (`PhoneMinigame.DoSaveGameFlow`) also sets `GlobalReferences.Instance.Variables.Mechanics.PV_PlayerHasSaved = true` after raising the save. The plugin mirrors this in a `Hide` Postfix when `m_selectedSaveIndex >= 0`.
- `GameMenuState.IsInAnyMenu()` covers menus, minigames, dialogue, video cutscenes, examine, game over and similar. Find the instance with `Resources.FindObjectsOfTypeAll`.

### Load-time phone call (PlayerFSM)
- After a load, the player's PlayMaker FSM `PlayerFSM` (on GameObject `Player`) runs:
  `CutsceneUsePhone` (pose `Player_InteractPhone_Idle`, waits) -GameStart-> `LoadGamePhoneCall` (custom action `Actions.LoadGameContinuePhoneCall`) -FINISHED-> `UsePhoneExit` (anim `Player_InteractPhone_Exit`) -Reset-> `Idle`.
- The **main-menu background is the live scene of the latest save**, with the player waiting in `CutsceneUsePhone` until "Continue" sends `GameStart`.
- `LoadGameContinuePhoneCall` calls `PhoneBook.GetSaveNumber()`, then `GetSavePhoneEvent()`, which looks up the event *by name* from `PhoneMemory.m_savePhoneEvent` (a ScriptableObject string that is saved with the game). It then runs `PhoneInteraction.CallNumber(..., ignoreSave: true)`. With an unknown or null event name, `CallNumber` plays `PhoneBook.m_invalidPhoneCallDialogue` (or logs an error) and finishes without getting stuck. That's why hotkey saves still load if the mod is removed.
- Global FSM transitions worth knowing: `Reset -> Idle`, `Cutscene/UsePhone -> CutsceneUsePhone`, `Dead -> Dead`, `ScriptControlled/Enable -> ScriptControlled`, plus many grab, ledge and transition events.

### How the plugin uses this
- **Hotkey save marker:** right before `SaveDuringGameMenu.Show()`, set `PhoneMemory.m_savePhoneEvent = "__SaveAnywhere_HotkeySave__"`, then restore the old value in the `Hide` Postfix. The saved file therefore remembers that it was a hotkey save.
- **On load:** `HotkeySaveLoadWatcher.Tick()` (each frame, from the injected MonoBehaviour) checks for marker + `PlayerFSM` in `CutsceneUsePhone`. When both hold, it clears the marker and calls `SendEvent("Reset")`, so the phone pose never shows, not even behind the main menu. The `LoadGameContinuePhoneCall.OnEnter` Prefix does the same as a fallback, and uses `Fsm.Event("Reset")` rather than `Finish()`, because `Finish()` would play the hang-up animation.
- **Safety:** the hotkey requires `!IsInAnyMenu()`, `PlayerHealth > 0`, and the `PlayerFSM` active state to be in `AllowedPlayerStates` (default `Idle, Walk, WalkStart, WalkInAnimTime, Sprint, SprintStart, SprintEnd, Turn, Turn Run, CrouchFSM`). Blocked attempts log the state name. If a legit state is missing, add it to the default.

## IL2CPP / BepInEx gotchas

- **Identical code folding:** IL2CPP merges identical method bodies. For example, `PhoneMinigame.OnSaveCompletedSuccesfully` and `OnSaveFailed` share one address, so patching one patches both. Before patching, check that no other method has the same RVA (`dis.py --find`, or the symbol cache's `byname` map). Tiny getters and setters are almost always shared.
- Il2CppInterop exposes private fields as properties (`__instance.m_phoneBook`) and private methods as public. Nested and generic types get mangled names. Custom PlayMaker actions are in namespace `Actions`.
- A MonoBehaviour must be registered with `ClassInjector.RegisterTypeInIl2Cpp<T>()` before `AddComponent<T>()`. For input, use `BepInEx.UnityInput.Current` (it works with both the legacy and the new input system).
- Field offsets from Cpp2IL include the object header. Unity objects start fields at 0x18/0x20.

## Re-doing the reverse engineering

The game's code can only be read from native code. This is the workflow that worked:

1. **Find names:** `python tools/il2cpp-re/strs.py "<game>\Carpenter_Data\il2cpp_data\Metadata\global-metadata.dat" "<regex>"`.
2. **Signatures:** ilspycmd on the BepInEx interop DLLs (`ilspycmd -p -o out -r <game>\BepInEx\interop <game>\BepInEx\interop\CarpenterScripts.dll`). This gives signatures and fields only; the method bodies are stubs.
3. **Addresses and offsets:** Cpp2IL CLI (`2022.1.0-pre-release.21`+, which supports metadata v39; the old Il2CppDumper does not):
   ```
   Cpp2IL.exe --game-path "<game>" --exe-name Carpenter --use-processor attributeinjector --output-as dummydll --output-to <out>\dll
   ilspycmd -p -o <out>\src\CarpenterScripts <out>\dll\CarpenterScripts.dll   (repeat for UnityEngine.CoreModule etc. if needed)
   ```
   The dummy sources carry `[Address(RVA=...)]` and `[FieldOffset(...)]`.
4. **Read the code** (Python venv with `tools/il2cpp-re/requirements.txt`; run with `python -I`):
   ```
   python tools/il2cpp-re/dis.py <out>\src - --find "PhoneMinigame::"
   python tools/il2cpp-re/dis.py <out>\src - --fields PhoneMinigame
   python tools/il2cpp-re/dis.py <out>\src "<game>\GameAssembly.dll" PhoneMinigame::Call 0x7223C0
   python tools/il2cpp-re/xref.py <out>\src "<game>\GameAssembly.dll" 0x7213E0
   ```
   Calls are annotated with method names when the target is a known symbol. RVAs change with every game update, so regenerate after patches.
5. **PlayMaker FSMs** are asset data and are not in the code. The fastest way to inspect one is at runtime: temporarily log `fsm.states` (name, actions with their fields via `GetIl2CppType().GetFields(...)`, transitions) and `globalTransitions`, plus `activeStateName` changes, from a patch or the MonoBehaviour. The first version of the load-call fix was found this way.
6. Ghidra (with the ghidra MCP) is available on the owner's machine, but it wasn't needed. The scripts above are much faster for targeted questions.
