# Fish Hunters Mod Menu

DLL patcher for **Fish Hunters: Most Lethal Fishing Simulator** on Steam. Injects an in-game mod menu (F8) that lets you toggle all cheats live, plus re-enables the hidden developer cheat menu (F9).

Built for singleplayer use only. Fish Hunters is a Unity game using FishNet networking even in singleplayer (you host locally), so all patches target server-side logic that runs on your machine.

## Features

Press **F8** in-game to open the mod menu (centered on screen). All cheats are **disabled by default** — click a toggle to enable it, click again to disable.

| Toggle | Description |
|--------|-------------|
| Free Purchases | `CanSpend()` returns true and `Spend()` is skipped. Buy anything for free. |
| Boost Currency (999999) | `Add()` always adds 999999 regardless of the original amount. |
| Max Level | `GetLevelForExp()` returns the highest level in the config. |
| Infinite Ammo | `GetBulletsCount()` always returns 999. |
| No Ammo Cost | `CostBullets()` is skipped. Ammo is never consumed. |
| 3x Inventory | `HandleCreateEntity()` multiplies all container capacities by 3. Always on — applies at scene load. |

Additionally, **F9** opens the hidden developer cheat menu with teleportation, fishing stage controls, and entity tools.

## Requirements

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later

## Usage

Close the game first, then run the patcher:

```
cd Patcher
dotnet run
```

The patcher auto-detects the game installation on common Steam library paths. If it can't find the game, pass the path manually:

```
dotnet run -- "E:\SteamLibrary\steamapps\common\Fish Hunters\FishTmp_Data\Managed"
```

Start the game normally after patching. Press **F8** for the mod menu, **F9** for the dev cheat menu.

### Restoring the Original DLL

The patcher creates `Assembly-CSharp.dll.backup` before modifying anything. To restore:

```
cd "D:\SteamLibrary\steamapps\common\Fish Hunters\FishTmp_Data\Managed"
copy Assembly-CSharp.dll.backup Assembly-CSharp.dll
```

Or use Steam's "Verify integrity of game files" option.

## How It Works

The patcher modifies IL bytecode in `Assembly-CSharp.dll` using Mono.Cecil. It injects two new types:

- **ModMenuConfig** — static class with boolean toggle fields for each cheat (off by default, except 3x Inventory)
- **ModMenuController** — a `MonoBehaviour` bootstrapped from `InputServerCheatMenu.Tick()`, renders a centered Unity IMGUI menu on F8 and manages cursor lock/unlock

Each gameplay patch is **conditional**: a branch at the start of the target method checks the corresponding `ModMenuConfig` toggle. When enabled, the modded behavior runs. When disabled, the original game code executes normally.

The patcher always restores from backup before applying patches, so it's safe to re-run after changes.

## Disclaimer

For singleplayer and educational use only. Do not use in multiplayer sessions. Game updates may require re-running the patcher if `Assembly-CSharp.dll` is replaced by Steam.

## License

MIT
