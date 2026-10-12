# Silver Pines – Save Anywhere

A BepInEx plugin for [Silver Pines](https://store.steampowered.com/app/2333000/Silver_Pines/) that lets you save whenever you want, so you can stop playing at any time.

In the base game you save by putting coins into payphones, and coins are limited. This mod adds a **save hotkey (F5)** that opens the game's own save menu wherever you are. Payphones still work as normal, and you can optionally make them free as well.

## Features

- **Save anywhere with F5.** Opens the game's built-in save screen. It uses the same save slots and the same save routine as the payphones.
- **No phone call when loading a hotkey save.** Saves made at a payphone start with the character on the phone. Hotkey saves start with the character standing normally, both in-game and behind the main menu.
- **Safety checks.** The hotkey only works while you are standing, walking, sprinting, turning or crouching, and only if no menu, dialogue or cutscene is open. You can't save mid-jump, on a ladder, while grabbed or while dying.
- **Optional free payphones** (off by default). When turned on, payphones never need a coin. This makes every call free, not just the save number.

## Requirements

| | |
|---|---|
| Game | Silver Pines, Windows (Steam) |
| Mod loader | **BepInEx 6 for Unity IL2CPP, bleeding-edge build `be.788` or newer** |

> BepInEx 5 and the "stable" BepInEx 6 pre-releases do **not** work. Silver Pines is a Unity 6 IL2CPP game and needs a recent BepInEx 6 bleeding-edge IL2CPP build. The all-in-one download below already includes the right one.

Tested with game build `25827516` (Unity 6000.3.15f1). A game update can break the mod. If it does, please open an issue.

## Installation

### Option A – All-in-one (recommended if you don't have BepInEx yet)

1. Download `SilverPines-SaveAnywhere-vX.Y.Z-with-BepInEx.zip` from [Releases](../../releases/latest).
2. Find the game folder: in Steam, right-click Silver Pines, then **Manage → Browse local files**. It contains `Carpenter.exe`.
3. Extract the zip into that folder. `winhttp.dll`, `doorstop_config.ini` and the `BepInEx` and `dotnet` folders should end up next to `Carpenter.exe`.
4. Start the game. **The first launch takes a few minutes** while BepInEx prepares itself. It may look frozen, so just wait for the main menu.

### Option B – BepInEx 6 IL2CPP is already installed

1. Download `SilverPines-SaveAnywhere-vX.Y.Z.zip` from [Releases](../../releases/latest).
2. Extract it into the game folder, so the DLL ends up in `BepInEx\plugins\SilverPines.SaveAnywhere\`.

### Steam Deck / Linux (Proton)

Add this to the game's Steam launch options so BepInEx is loaded:

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

### Checking that it works

After the game has been started once, `BepInEx\LogOutput.log` should contain a line like:

```
[Info   :Silver Pines - Save Anywhere] Loaded. FreePhoneCalls=False, SaveAnywhere=True (F5), ...
```

## Usage

- Press **F5** while walking around to open the save menu, then pick a slot like you would at a payphone.
- If nothing happens, you're probably in a state where saving is blocked: in a menu, mid-jump, on a ladder, in a cutscene and so on. The reason is written to `BepInEx\LogOutput.log`.

## Configuration

The config file `BepInEx\config\com.mariustbjercke.silverpines.saveanywhere.cfg` is created on first launch.

| Setting | Default | Description |
|---|---|---|
| `General.SaveAnywhereEnabled` | `true` | Enables the save hotkey. |
| `General.SaveAnywhereKey` | `F5` | The hotkey. Any Unity `KeyCode`, e.g. `F6`, `F9`, `Home`. |
| `General.SkipPhoneCallAfterHotkeySave` | `true` | Skips the phone pose and conversation when loading a hotkey save. |
| `General.FreePhoneCalls` | `false` | Payphones no longer need a coin. This makes **all** calls free. |
| `Safety.AllowedPlayerStates` | `Idle, Walk, …` | Player states in which the hotkey works. Leave empty to allow any state, at your own risk. |

## Good to know

- **Back up your saves** before trying any mod. They're in `%USERPROFILE%\AppData\LocalLow\Wych Elm\Silver Pines\`.
- Hotkey saves use the same slots as payphone saves, so you can overwrite one with the other.
- Saving in the middle of a dangerous situation is allowed, for example with an enemy right next to you. Choose your moments.
- **Uninstalling:** delete `BepInEx\plugins\SilverPines.SaveAnywhere\`. To remove BepInEx completely, also delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `changelog.txt` and the `BepInEx` and `dotnet` folders. Hotkey saves still load without the mod, but the game may then play its "invalid number" phone line when they load.

## How it works

The game already contains everything needed. The mod mostly switches things on:

- `SaveDuringGameMenu` is a complete "save from menu" screen in the game code that is never opened. The hotkey opens it. It raises the same save event as the payphone and also sets `PV_PlayerHasSaved`, like the payphone does.
- On load, the player's PlayMaker state machine waits in `CutsceneUsePhone` and then replays the phone call stored in the save (`PhoneMemory.m_savePhoneEvent`). Hotkey saves store a marker there instead. When the mod sees that marker, it sends the state machine's own `Reset` event, which goes straight to `Idle`.
- `PhoneMinigame` only allows dialing once a coin has been inserted. With `FreePhoneCalls` on, that requirement is cleared when the phone opens.

## Building from source

Requirements: the [.NET SDK](https://dotnet.microsoft.com/download) (6.0 or newer), and the game with BepInEx 6 IL2CPP installed and launched once. The build references the interop assemblies BepInEx generates in `BepInEx\interop`.

1. Point the build at your game folder by creating `Local.props` in the repository root:

   ```xml
   <Project>
     <PropertyGroup>
       <GameDir>D:\SteamLibrary\steamapps\common\Silver Pines</GameDir>
     </PropertyGroup>
   </Project>
   ```

   Without it, the default Steam path `C:\Program Files (x86)\Steam\steamapps\common\Silver Pines` is used.
2. Run `dotnet build -c Release`. The DLL is copied into the game's `BepInEx\plugins\SilverPines.SaveAnywhere\` automatically. Pass `-p:DeployToGame=false` to skip that step.
3. Run `.\scripts\package.ps1` to create the release zips in `dist\`.

## License

[MIT](LICENSE). Not affiliated with Wych Elm or Team17. BepInEx, which is included in the all-in-one download, is licensed under the [LGPL-2.1](https://github.com/BepInEx/BepInEx/blob/master/LICENSE).
