using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

string gameDir = FindGameDirectory();
string dllPath = Path.Combine(gameDir, "Assembly-CSharp.dll");
string backupPath = dllPath + ".backup";

if (!File.Exists(backupPath))
{
    File.Copy(dllPath, backupPath);
    Console.WriteLine($"[+] Backup created: {backupPath}");
}
else
{
    Console.WriteLine("[*] Backup already exists, skipping.");
}

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(gameDir);
var assembly = AssemblyDefinition.ReadAssembly(dllPath,
    new ReaderParameters { AssemblyResolver = resolver });
var module = assembly.MainModule;

int patchCount = 0;

Console.WriteLine("\n[*] Patch 1: Developer cheat menu (F9 toggle + cursor unlock)...");
PatchCheatMenu();

Console.WriteLine("[*] Patch 2: Free purchases (CanSpend bypass)...");
var currencyType = module.Types.FirstOrDefault(
    t => t.FullName == "AppRoot.Player.Currency.PlayerCurrencyProvider");
PatchMethodReturnTrue(currencyType, "CanSpend");

Console.WriteLine("[*] Patch 3: No currency deduction (Spend bypass)...");
PatchMethodReturnVoid(currencyType, "Spend");

Console.WriteLine("[*] Patch 4: Infinite currency (TryGetCurrency + GetAmount)...");
PatchTryGetCurrency(currencyType);
PatchGetAmount(currencyType);

Console.WriteLine("[*] Patch 5: 10x inventory slot capacity...");
PatchInventoryCapacity();

if (patchCount > 0)
{
    string patchedPath = dllPath + ".patched";
    assembly.Write(patchedPath);
    assembly.Dispose();
    File.Delete(dllPath);
    File.Move(patchedPath, dllPath);
    Console.WriteLine($"\n[+] Done. {patchCount} patches applied.");
    Console.WriteLine("[+] Restart the game for changes to take effect.");
    Console.WriteLine("[*] To restore: rename Assembly-CSharp.dll.backup to Assembly-CSharp.dll");
}
else
{
    assembly.Dispose();
    Console.WriteLine("\n[!] No patches applied.");
}

// -------------------------------------------------------

void PatchCheatMenu()
{
    var inputType = module.Types.FirstOrDefault(
        t => t.FullName == "Ui.UiRoots.InputServerCheatMenu");
    var tickMethod = inputType?.Methods.FirstOrDefault(m => m.Name == "Tick");
    if (tickMethod == null) { Console.WriteLine("    [!] InputServerCheatMenu.Tick not found"); return; }

    // Resolve Unity methods
    var unityInput = resolver.Resolve(
        AssemblyNameReference.Parse("UnityEngine.InputLegacyModule"));
    var getKeyDown = unityInput.MainModule.Types
        .First(t => t.FullName == "UnityEngine.Input")
        .Methods.First(m => m.Name == "GetKeyDown" && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.FullName == "UnityEngine.KeyCode");

    var unityCoreModule = resolver.Resolve(
        AssemblyNameReference.Parse("UnityEngine.CoreModule"));
    var cursorType = unityCoreModule.MainModule.Types
        .First(t => t.FullName == "UnityEngine.Cursor");
    var setLockState = cursorType.Methods.First(m => m.Name == "set_lockState");
    var setVisible = cursorType.Methods.First(m => m.Name == "set_visible");

    var uiManagerField = inputType!.Fields.First(f => f.Name == "_uiManager");
    var uiManagerIface = uiManagerField.FieldType.Resolve()!;
    var openMethod = uiManagerIface.Methods.First(m => m.Name == "Open" && m.Parameters.Count == 1);

    var checkUiField = inputType.Fields.First(f => f.Name == "_checkUi");
    var checkIface = checkUiField.FieldType.Resolve()!;
    var isAnyOpen = checkIface.Methods.First(m => m.Name == "get_IsAnyDialogOpen");

    var il = tickMethod.Body.GetILProcessor();
    tickMethod.Body.Instructions.Clear();

    // if (!Input.GetKeyDown(KeyCode.F9)) return;
    var ret = il.Create(OpCodes.Ret);
    il.Append(il.Create(OpCodes.Ldc_I4, 290)); // KeyCode.F9
    il.Append(il.Create(OpCodes.Call, module.ImportReference(getKeyDown)));
    il.Append(il.Create(OpCodes.Brfalse, ret));

    // Cursor.lockState = CursorLockMode.None (0)
    il.Append(il.Create(OpCodes.Ldc_I4_0));
    il.Append(il.Create(OpCodes.Call, module.ImportReference(setLockState)));

    // Cursor.visible = true
    il.Append(il.Create(OpCodes.Ldc_I4_1));
    il.Append(il.Create(OpCodes.Call, module.ImportReference(setVisible)));

    // if (_checkUi.IsAnyDialogOpen) return;  (don't stack menus)
    il.Append(il.Create(OpCodes.Ldarg_0));
    il.Append(il.Create(OpCodes.Ldfld, checkUiField));
    il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(isAnyOpen)));
    il.Append(il.Create(OpCodes.Brtrue, ret));

    // _uiManager.Open(UiElementType.ServerCheatMenu)
    il.Append(il.Create(OpCodes.Ldarg_0));
    il.Append(il.Create(OpCodes.Ldfld, uiManagerField));
    il.Append(il.Create(OpCodes.Ldc_I4, 1001)); // ServerCheatMenu
    il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(openMethod)));

    il.Append(ret);

    patchCount++;
    Console.WriteLine("    [OK] F9 toggles cheat menu with cursor unlock");

    // Patch CheatMenuScreen.OnHide to re-lock cursor when menu closes
    var cheatMenuType = module.Types.FirstOrDefault(t => t.FullName == "CheatMenuScreen");
    var onHide = cheatMenuType?.Methods.FirstOrDefault(m => m.Name == "OnHide");
    if (onHide != null)
    {
        var hideIl = onHide.Body.GetILProcessor();
        var firstInstr = onHide.Body.Instructions[0];

        // Insert at the start: Cursor.lockState = Locked (1); Cursor.visible = false;
        hideIl.InsertBefore(firstInstr, hideIl.Create(OpCodes.Ldc_I4_1)); // CursorLockMode.Locked
        hideIl.InsertBefore(firstInstr, hideIl.Create(OpCodes.Call, module.ImportReference(setLockState)));
        hideIl.InsertBefore(firstInstr, hideIl.Create(OpCodes.Ldc_I4_0)); // false
        hideIl.InsertBefore(firstInstr, hideIl.Create(OpCodes.Call, module.ImportReference(setVisible)));

        patchCount++;
        Console.WriteLine("    [OK] OnHide re-locks cursor when cheat menu closes");
    }
}

void PatchTryGetCurrency(TypeDefinition? type)
{
    // bool TryGetCurrency(Id currencyId, out int amount)
    // Patch to: amount = 999999; return true;
    var method = type?.Methods.FirstOrDefault(m => m.Name == "TryGetCurrency");
    if (method == null) { Console.WriteLine("    [!] TryGetCurrency not found"); return; }

    var il = method.Body.GetILProcessor();
    method.Body.Instructions.Clear();
    // arg0 = this, arg1 = currencyId, arg2 = out int amount (by ref)
    il.Append(il.Create(OpCodes.Ldarg_2));          // load ref to 'amount'
    il.Append(il.Create(OpCodes.Ldc_I4, 999999));   // push 999999
    il.Append(il.Create(OpCodes.Stind_I4));          // *amount = 999999
    il.Append(il.Create(OpCodes.Ldc_I4_1));          // return true
    il.Append(il.Create(OpCodes.Ret));
    patchCount++;
    Console.WriteLine("    [OK] TryGetCurrency always outputs 999999");
}

void PatchGetAmount(TypeDefinition? type)
{
    var method = type?.Methods.FirstOrDefault(m => m.Name == "GetAmount");
    if (method == null) { Console.WriteLine("    [!] GetAmount not found"); return; }

    var il = method.Body.GetILProcessor();
    method.Body.Instructions.Clear();
    il.Append(il.Create(OpCodes.Ldc_I4, 999999));
    il.Append(il.Create(OpCodes.Ret));
    patchCount++;
    Console.WriteLine("    [OK] GetAmount always returns 999999");
}

void PatchMethodReturnTrue(TypeDefinition? type, string methodName)
{
    var method = type?.Methods.FirstOrDefault(m => m.Name == methodName);
    if (method == null) { Console.WriteLine($"    [!] {methodName} not found"); return; }

    var il = method.Body.GetILProcessor();
    method.Body.Instructions.Clear();
    il.Append(il.Create(OpCodes.Ldc_I4_1));
    il.Append(il.Create(OpCodes.Ret));
    patchCount++;
    Console.WriteLine($"    [OK] {methodName} now always returns true");
}

void PatchMethodReturnVoid(TypeDefinition? type, string methodName)
{
    var method = type?.Methods.FirstOrDefault(m => m.Name == methodName);
    if (method == null) { Console.WriteLine($"    [!] {methodName} not found"); return; }

    var il = method.Body.GetILProcessor();
    method.Body.Instructions.Clear();
    il.Append(il.Create(OpCodes.Ret));
    patchCount++;
    Console.WriteLine($"    [OK] {methodName} is now a no-op");
}

void PatchInventoryCapacity()
{
    var type = module.Types.FirstOrDefault(
        t => t.FullName == "Features.SlotsContainer.SlotContainerController");
    var method = type?.Methods.FirstOrDefault(m => m.Name == "HandleCreateEntity");
    if (method == null) { Console.WriteLine("    [!] HandleCreateEntity not found"); return; }

    var instructions = method.Body.Instructions;
    for (int i = 0; i < instructions.Count; i++)
    {
        if (instructions[i].OpCode == OpCodes.Ldfld
            && instructions[i].Operand is FieldReference f
            && f.Name == "Capacity")
        {
            var il = method.Body.GetILProcessor();
            var load10 = il.Create(OpCodes.Ldc_I4, 10);
            var mul = il.Create(OpCodes.Mul);
            il.InsertAfter(instructions[i], load10);
            il.InsertAfter(load10, mul);
            patchCount++;
            Console.WriteLine("    [OK] All container capacities multiplied by 10");
            return;
        }
    }
    Console.WriteLine("    [!] Capacity field reference not found");
}

string FindGameDirectory()
{
    string[] searchPaths = [
        @"D:\SteamLibrary\steamapps\common",
        @"C:\Program Files (x86)\Steam\steamapps\common",
        @"C:\Program Files\Steam\steamapps\common",
        @"E:\SteamLibrary\steamapps\common",
    ];

    string[] folderNames = [
        "Fish Hunters \U0001F41F",
        "Fish Hunters",
    ];

    foreach (var basePath in searchPaths)
    {
        foreach (var folder in folderNames)
        {
            string candidate = Path.Combine(basePath, folder, "FishTmp_Data", "Managed");
            if (Directory.Exists(candidate))
                return candidate;
        }
    }

    var args = Environment.GetCommandLineArgs();
    if (args.Length > 1 && Directory.Exists(args[1]))
        return args[1];

    Console.WriteLine("Could not find Fish Hunters installation.");
    Console.WriteLine("Pass the path to FishTmp_Data/Managed as an argument.");
    Environment.Exit(1);
    return "";
}
