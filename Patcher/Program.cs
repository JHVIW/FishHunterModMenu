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

// --- Patch 1: Re-enable developer cheat menu on F9 ---
Console.WriteLine("\n[*] Patch 1: Developer cheat menu (F9)...");
PatchCheatMenu();

// --- Patch 2: CanSpend always returns true ---
Console.WriteLine("[*] Patch 2: Free purchases (CanSpend bypass)...");
var currencyType = module.Types.FirstOrDefault(
    t => t.FullName == "AppRoot.Player.Currency.PlayerCurrencyProvider");
PatchMethodReturnTrue(currencyType, "CanSpend");

// --- Patch 3: Spend does nothing ---
Console.WriteLine("[*] Patch 3: No currency deduction (Spend bypass)...");
PatchMethodReturnVoid(currencyType, "Spend");

// --- Patch 4: 10x inventory capacity ---
Console.WriteLine("[*] Patch 4: 10x inventory slot capacity...");
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

    var unityInput = resolver.Resolve(
        AssemblyNameReference.Parse("UnityEngine.InputLegacyModule"));
    var getKeyDown = unityInput.MainModule.Types
        .First(t => t.FullName == "UnityEngine.Input")
        .Methods.First(m => m.Name == "GetKeyDown" && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.FullName == "UnityEngine.KeyCode");

    var uiManagerField = inputType!.Fields.First(f => f.Name == "_uiManager");
    var openMethod = uiManagerField.FieldType.Resolve()!
        .Methods.First(m => m.Name == "Open" && m.Parameters.Count == 1);

    var il = tickMethod.Body.GetILProcessor();
    tickMethod.Body.Instructions.Clear();

    var ret = il.Create(OpCodes.Ret);
    il.Append(il.Create(OpCodes.Ldc_I4, 290)); // KeyCode.F9
    il.Append(il.Create(OpCodes.Call, module.ImportReference(getKeyDown)));
    il.Append(il.Create(OpCodes.Brfalse_S, ret));
    il.Append(il.Create(OpCodes.Ldarg_0));
    il.Append(il.Create(OpCodes.Ldfld, uiManagerField));
    il.Append(il.Create(OpCodes.Ldc_I4, 1001)); // UiElementType.ServerCheatMenu
    il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(openMethod)));
    il.Append(ret);

    patchCount++;
    Console.WriteLine("    [OK] Press F9 in-game to open cheat menu");
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

    // Check for folder with the fish emoji name
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

    // Fallback: check if user passed a path as argument
    var args = Environment.GetCommandLineArgs();
    if (args.Length > 1 && Directory.Exists(args[1]))
        return args[1];

    Console.WriteLine("Could not find Fish Hunters installation.");
    Console.WriteLine("Pass the path to FishTmp_Data/Managed as an argument.");
    Environment.Exit(1);
    return "";
}
