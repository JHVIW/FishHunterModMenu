# Fish Hunters Mod Menu

DLL patcher for **Fish Hunters: Most Lethal Fishing Simulator** on Steam. Re-enables the hidden developer cheat menu, gives infinite currency, removes all purchase costs, and expands inventory capacity by 10x.

Built for singleplayer use only. Fish Hunters is a Unity game using FishNet networking even in singleplayer (you host locally), so all patches target server-side logic that runs on your machine.

## Features

Patches the game's `Assembly-CSharp.dll` directly using Mono.Cecil. A backup is created automatically before any changes.

| Patch | Description |
|-------|-------------|
| Developer Cheat Menu | Re-enables the hidden `ServerCheatMenuDialog`. Press **F9** to open it with automatic cursor unlock. Includes teleportation, fishing stage controls, and entity tools. |
| Infinite Currency | `PlayerCurrencyProvider.GetAmount()` always returns 999999. Your balance displays as 999999 everywhere. |
| Free Purchases | `PlayerCurrencyProvider.CanSpend()` always returns `true`. Buy anything regardless of actual balance. |
| No Currency Deduction | `PlayerCurrencyProvider.Spend()` is a no-op. Currency is never subtracted. |
| 10x Inventory Capacity | `SlotContainerController.HandleCreateEntity()` multiplies all `ItemContainerConfig.Capacity` values by 10. Your 9 storage slots become 90. |

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

Start the game normally after patching. Press **F9** to open the developer cheat menu.

### Restoring the Original DLL

The patcher creates `Assembly-CSharp.dll.backup` before modifying anything. To restore:

```
cd "D:\SteamLibrary\steamapps\common\Fish Hunters\FishTmp_Data\Managed"
copy Assembly-CSharp.dll.backup Assembly-CSharp.dll
```

Or use Steam's "Verify integrity of game files" option.

## How It Works

Fish Hunters is built on Unity with FishNet for networking. Even in singleplayer, the game runs a local server. The patcher modifies IL bytecode in `Assembly-CSharp.dll` using Mono.Cecil to:

- Inject `Input.GetKeyDown(KeyCode.F9)` into the previously empty `InputServerCheatMenu.Tick()` method, which calls `Cursor.lockState = None`, `Cursor.visible = true`, and `IUiManager.Open(ServerCheatMenu)` when pressed
- Replace `PlayerCurrencyProvider.GetAmount()` with `ldc.i4 999999; ret`
- Replace `PlayerCurrencyProvider.CanSpend()` with `ldc.i4.1; ret` (always true)
- Replace `PlayerCurrencyProvider.Spend()` with `ret` (no-op)
- Insert `ldc.i4 10; mul` after `ldfld Capacity` in `SlotContainerController.HandleCreateEntity()`

## Disclaimer

For singleplayer and educational use only. Do not use in multiplayer sessions. Game updates may require re-running the patcher if `Assembly-CSharp.dll` is replaced by Steam.

## License

MIT
