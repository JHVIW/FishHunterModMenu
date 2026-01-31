# Fish Hunters Mod Menu

Mod menu and DLL patcher for **Fish Hunters: Most Lethal Fishing Simulator** on Steam. Includes an Assembly-CSharp patcher that re-enables the hidden developer cheat menu, removes currency costs, and expands inventory capacity, plus a standalone runtime memory scanner for finding and editing any value in the game process.

Built for singleplayer use only. Fish Hunters is a Unity game using FishNet networking even in singleplayer (you host locally), so all patches target server-side logic that runs on your machine.

## Features

### DLL Patcher (`Patcher/`)

Patches the game's `Assembly-CSharp.dll` directly using Mono.Cecil. A backup is created automatically before any changes.

| Patch | Description |
|-------|-------------|
| Developer Cheat Menu | Re-enables the hidden `ServerCheatMenuDialog` — press **F9** in-game to open it. Includes teleportation, fishing stage controls, and entity extraction tools. |
| Free Purchases | `PlayerCurrencyProvider.CanSpend()` always returns `true` — buy anything regardless of balance. |
| No Currency Deduction | `PlayerCurrencyProvider.Spend()` is replaced with a no-op — your currency is never subtracted. |
| 10x Inventory Capacity | `SlotContainerController.HandleCreateEntity()` multiplies all `ItemContainerConfig.Capacity` values by 10 at runtime. Your 9 storage slots become 90. |

### Memory Scanner (`memory_scanner.py`)

A GUI memory scanner (like a minimal Cheat Engine) that attaches to the running game process. Useful for finding and modifying any value the patcher doesn't cover.

- Scan by exact value or take a full memory snapshot
- Filter by changed, unchanged, increased, or decreased values
- Write new values to found addresses
- Freeze addresses to continuously write a value (100ms interval)
- Supports int16, int32, int64, float32, and float64 data types

## Requirements

**DLL Patcher:**
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later

**Memory Scanner:**
- Python 3.10+
- `pymem` (`pip install pymem`)
- Must be run as Administrator

## Usage

### DLL Patcher

Close the game first, then run the patcher:

```
cd Patcher
dotnet run
```

The patcher auto-detects the game installation on common Steam library paths (C: and D: drives). If it can't find the game, pass the path manually:

```
dotnet run -- "E:\SteamLibrary\steamapps\common\Fish Hunters\FishTmp_Data\Managed"
```

Start the game normally after patching. Press **F9** to open the developer cheat menu.

### Memory Scanner

Start the game first, then run the scanner as Administrator:

```
pip install pymem
python memory_scanner.py
```

**Finding a value (e.g. currency):**

1. Note your current in-game currency (e.g. `1500`)
2. Enter `1500` in the Value field, click **First Scan**
3. Spend or earn currency in-game so it changes (e.g. to `1350`)
4. Enter `1350`, click **Next Scan**
5. Repeat until only a few addresses remain
6. Select the correct address, enter a new value, click **Write**

**Unknown value scan:**

1. Leave the Value field empty, click **First Scan** (takes a snapshot of all memory)
2. Change the value in-game
3. Click **Changed**, **Increased**, or **Decreased** to filter
4. Repeat until narrowed down

### Restoring the Original DLL

The patcher creates `Assembly-CSharp.dll.backup` before modifying anything. To restore:

```
cd "D:\SteamLibrary\steamapps\common\Fish Hunters\FishTmp_Data\Managed"
copy Assembly-CSharp.dll.backup Assembly-CSharp.dll
```

Or use Steam's "Verify integrity of game files" option.

## How It Works

Fish Hunters is built on Unity with FishNet for networking. Even in singleplayer, the game runs a local server. The patcher modifies IL bytecode in `Assembly-CSharp.dll` using Mono.Cecil to:

- Inject an `Input.GetKeyDown(KeyCode.F9)` check into the previously empty `InputServerCheatMenu.Tick()` method, calling `IUiManager.Open(UiElementType.ServerCheatMenu)` when pressed
- Replace `PlayerCurrencyProvider.CanSpend()` with a single `ldc.i4.1; ret` (always true)
- Replace `PlayerCurrencyProvider.Spend()` with a single `ret` (no-op)
- Insert `ldc.i4 10; mul` after the `ldfld Capacity` instruction in `SlotContainerController.HandleCreateEntity()`

The memory scanner uses `VirtualQueryEx` to enumerate readable memory regions and performs byte-pattern matching to locate values in the game process.

## Disclaimer

For singleplayer and educational use only. Do not use in multiplayer sessions. Game updates may require re-running the patcher if `Assembly-CSharp.dll` is replaced by Steam.

## License

MIT
