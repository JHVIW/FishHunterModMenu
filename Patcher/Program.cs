using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

string gameDir = FindGameDirectory();
string dllPath = Path.Combine(gameDir, "Assembly-CSharp.dll");
string backupPath = dllPath + ".backup";

// Always restore from backup before patching to avoid double-patching
if (File.Exists(backupPath))
{
    File.Copy(backupPath, dllPath, overwrite: true);
    Console.WriteLine("[*] Restored clean DLL from backup.");
}
else
{
    File.Copy(dllPath, backupPath);
    Console.WriteLine("[+] Backup created: " + backupPath);
}

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(gameDir);
var assembly = AssemblyDefinition.ReadAssembly(dllPath,
    new ReaderParameters { AssemblyResolver = resolver });
var module = assembly.MainModule;

// ── Resolve Unity assemblies ──────────────────────────────────────────
var unityCoreAsm = resolver.Resolve(AssemblyNameReference.Parse("UnityEngine.CoreModule"));
var unityInputAsm = resolver.Resolve(AssemblyNameReference.Parse("UnityEngine.InputLegacyModule"));
var unityImguiAsm = resolver.Resolve(AssemblyNameReference.Parse("UnityEngine.IMGUIModule"));

var monoBehaviourDef = unityCoreAsm.MainModule.Types.First(t => t.FullName == "UnityEngine.MonoBehaviour");
var gameObjectDef = unityCoreAsm.MainModule.Types.First(t => t.FullName == "UnityEngine.GameObject");
var unityObjectDef = unityCoreAsm.MainModule.Types.First(t => t.FullName == "UnityEngine.Object");
var rectDef = unityCoreAsm.MainModule.Types.First(t => t.FullName == "UnityEngine.Rect");
var cursorDef = unityCoreAsm.MainModule.Types.First(t => t.FullName == "UnityEngine.Cursor");
var inputDef = unityInputAsm.MainModule.Types.First(t => t.FullName == "UnityEngine.Input");
var guiDef = unityImguiAsm.MainModule.Types.First(t => t.FullName == "UnityEngine.GUI");
var runtimeInitAttrDef = unityCoreAsm.MainModule.Types.First(t => t.FullName == "UnityEngine.RuntimeInitializeOnLoadMethodAttribute");

// Resolve methods we need
var getKeyDown = module.ImportReference(inputDef.Methods.First(m =>
    m.Name == "GetKeyDown" && m.Parameters.Count == 1
    && m.Parameters[0].ParameterType.FullName == "UnityEngine.KeyCode"));
var setLockState = module.ImportReference(cursorDef.Methods.First(m => m.Name == "set_lockState"));
var setVisible = module.ImportReference(cursorDef.Methods.First(m => m.Name == "set_visible"));
var rectCtor = module.ImportReference(rectDef.Methods.First(m =>
    m.IsConstructor && m.Parameters.Count == 4));
var guiBox = module.ImportReference(guiDef.Methods.First(m =>
    m.Name == "Box" && m.Parameters.Count == 2
    && m.Parameters[1].ParameterType.FullName == "System.String"));
var guiToggle = module.ImportReference(guiDef.Methods.First(m =>
    m.Name == "Toggle" && m.Parameters.Count == 3
    && m.Parameters[2].ParameterType.FullName == "System.String"));
var guiLabel = module.ImportReference(guiDef.Methods.First(m =>
    m.Name == "Label" && m.Parameters.Count == 2
    && m.Parameters[1].ParameterType.FullName == "System.String"));
var goCtorString = module.ImportReference(gameObjectDef.Methods.First(m =>
    m.IsConstructor && m.Parameters.Count == 1
    && m.Parameters[0].ParameterType.FullName == "System.String"));
var addComponentType = module.ImportReference(gameObjectDef.Methods.First(m =>
    m.Name == "AddComponent" && m.Parameters.Count == 1
    && m.Parameters[0].ParameterType.FullName == "System.Type"));
var dontDestroyOnLoad = module.ImportReference(unityObjectDef.Methods.First(m =>
    m.Name == "DontDestroyOnLoad"));
var runtimeInitCtor = module.ImportReference(runtimeInitAttrDef.Methods.First(m =>
    m.IsConstructor && m.Parameters.Count == 1));
var screenDef = unityCoreAsm.MainModule.Types.First(t => t.FullName == "UnityEngine.Screen");
var screenGetWidth = module.ImportReference(screenDef.Methods.First(m => m.Name == "get_width"));
var screenGetHeight = module.ImportReference(screenDef.Methods.First(m => m.Name == "get_height"));

// Resolve System.Type.GetTypeFromHandle from corlib
var corlib = module.TypeSystem.Object.Resolve().Module;
var systemTypeDef = corlib.Types.First(t => t.FullName == "System.Type");
var getTypeFromHandle = module.ImportReference(systemTypeDef.Methods.First(m =>
    m.Name == "GetTypeFromHandle"));

int patchCount = 0;

// ══════════════════════════════════════════════════════════════════════
// STEP 1: Inject ModMenuConfig (static class with toggle fields)
// ══════════════════════════════════════════════════════════════════════
Console.WriteLine("\n[*] Injecting ModMenuConfig...");

var configType = new TypeDefinition("", "ModMenuConfig",
    TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
    module.TypeSystem.Object);

string[] toggleNames =
{
    "FreePurchases", "NoCurrencyDeduction", "BoostCurrency",
    "MaxLevel", "InfiniteAmmo", "NoAmmoCost", "InventoryMultiplier"
};
var toggleFields = new Dictionary<string, FieldDefinition>();
foreach (var name in toggleNames)
{
    var f = new FieldDefinition(name,
        FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.Boolean);
    configType.Fields.Add(f);
    toggleFields[name] = f;
}
var menuOpenField = new FieldDefinition("MenuOpen",
    FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.Boolean);
configType.Fields.Add(menuOpenField);
var initializedField = new FieldDefinition("Initialized",
    FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.Boolean);
configType.Fields.Add(initializedField);

// Static constructor: all toggles OFF by default
var cctor = new MethodDefinition(".cctor",
    MethodAttributes.Static | MethodAttributes.Private |
    MethodAttributes.SpecialName | MethodAttributes.RTSpecialName | MethodAttributes.HideBySig,
    module.TypeSystem.Void);
{
    var il = cctor.Body.GetILProcessor();
    // bools default to false already, just need ret
    il.Append(il.Create(OpCodes.Ret));
}
configType.Methods.Add(cctor);
module.Types.Add(configType);
Console.WriteLine("    [OK] ModMenuConfig injected with 7 toggles");

// ══════════════════════════════════════════════════════════════════════
// STEP 2: Inject ModMenuController : MonoBehaviour
// ══════════════════════════════════════════════════════════════════════
Console.WriteLine("[*] Injecting ModMenuController...");

var controllerType = new TypeDefinition("", "ModMenuController",
    TypeAttributes.Public | TypeAttributes.BeforeFieldInit,
    module.ImportReference(monoBehaviourDef));

// Constructor: call base MonoBehaviour()
{
    var ctor = new MethodDefinition(".ctor",
        MethodAttributes.Public | MethodAttributes.SpecialName |
        MethodAttributes.RTSpecialName | MethodAttributes.HideBySig,
        module.TypeSystem.Void);
    var il = ctor.Body.GetILProcessor();
    il.Append(il.Create(OpCodes.Ldarg_0));
    il.Append(il.Create(OpCodes.Call, module.ImportReference(
        monoBehaviourDef.Methods.First(m => m.IsConstructor && m.Parameters.Count == 0))));
    il.Append(il.Create(OpCodes.Ret));
    controllerType.Methods.Add(ctor);
}

// Static Init() — called from Tick() bootstrap
{
    var init = new MethodDefinition("Init",
        MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
        module.TypeSystem.Void);
    var il = init.Body.GetILProcessor();
    // var go = new GameObject("FishHunterModMenu");
    il.Append(il.Create(OpCodes.Ldstr, "FishHunterModMenu"));
    il.Append(il.Create(OpCodes.Newobj, goCtorString));
    il.Append(il.Create(OpCodes.Dup));
    // go.AddComponent(typeof(ModMenuController));
    il.Append(il.Create(OpCodes.Ldtoken, controllerType));
    il.Append(il.Create(OpCodes.Call, getTypeFromHandle));
    il.Append(il.Create(OpCodes.Callvirt, addComponentType));
    il.Append(il.Create(OpCodes.Pop));
    // DontDestroyOnLoad(go);
    il.Append(il.Create(OpCodes.Call, dontDestroyOnLoad));
    il.Append(il.Create(OpCodes.Ret));
    controllerType.Methods.Add(init);
}

// Update() — F8 toggles menu + cursor
{
    var update = new MethodDefinition("Update",
        MethodAttributes.Private | MethodAttributes.HideBySig,
        module.TypeSystem.Void);
    var il = update.Body.GetILProcessor();
    var ret = il.Create(OpCodes.Ret);

    // if (!Input.GetKeyDown(KeyCode.F8)) return;   (F8 = 289)
    il.Append(il.Create(OpCodes.Ldc_I4, 289));
    il.Append(il.Create(OpCodes.Call, getKeyDown));
    il.Append(il.Create(OpCodes.Brfalse, ret));

    // MenuOpen = !MenuOpen
    il.Append(il.Create(OpCodes.Ldsfld, menuOpenField));
    il.Append(il.Create(OpCodes.Ldc_I4_0));
    il.Append(il.Create(OpCodes.Ceq));
    il.Append(il.Create(OpCodes.Stsfld, menuOpenField));

    // if (MenuOpen) unlock else lock cursor
    var lockLabel = il.Create(OpCodes.Ldc_I4_1);
    il.Append(il.Create(OpCodes.Ldsfld, menuOpenField));
    il.Append(il.Create(OpCodes.Brfalse, lockLabel));

    // Unlock cursor
    il.Append(il.Create(OpCodes.Ldc_I4_0));
    il.Append(il.Create(OpCodes.Call, setLockState));
    il.Append(il.Create(OpCodes.Ldc_I4_1));
    il.Append(il.Create(OpCodes.Call, setVisible));
    il.Append(il.Create(OpCodes.Br, ret));

    // Lock cursor
    il.Append(lockLabel); // Ldc_I4_1
    il.Append(il.Create(OpCodes.Call, setLockState));
    il.Append(il.Create(OpCodes.Ldc_I4_0));
    il.Append(il.Create(OpCodes.Call, setVisible));

    il.Append(ret);
    controllerType.Methods.Add(update);
}

// OnGUI() — draw centered mod menu
{
    var onGui = new MethodDefinition("OnGUI",
        MethodAttributes.Private | MethodAttributes.HideBySig,
        module.TypeSystem.Void);
    // Local variables: float ox, float oy (menu origin x/y)
    onGui.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Single)); // loc0 = ox
    onGui.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Single)); // loc1 = oy
    onGui.Body.InitLocals = true;

    var il = onGui.Body.GetILProcessor();
    var ret = il.Create(OpCodes.Ret);

    // if (!MenuOpen) return;
    il.Append(il.Create(OpCodes.Ldsfld, menuOpenField));
    il.Append(il.Create(OpCodes.Brfalse, ret));

    // ox = (Screen.width - 260) / 2
    il.Append(il.Create(OpCodes.Call, screenGetWidth));
    il.Append(il.Create(OpCodes.Ldc_I4, 260));
    il.Append(il.Create(OpCodes.Sub));
    il.Append(il.Create(OpCodes.Ldc_I4_2));
    il.Append(il.Create(OpCodes.Div));
    il.Append(il.Create(OpCodes.Conv_R4));
    il.Append(il.Create(OpCodes.Stloc_0));

    // oy = (Screen.height - 310) / 2
    il.Append(il.Create(OpCodes.Call, screenGetHeight));
    il.Append(il.Create(OpCodes.Ldc_I4, 310));
    il.Append(il.Create(OpCodes.Sub));
    il.Append(il.Create(OpCodes.Ldc_I4_2));
    il.Append(il.Create(OpCodes.Div));
    il.Append(il.Create(OpCodes.Conv_R4));
    il.Append(il.Create(OpCodes.Stloc_1));

    // Helper: emit new Rect(ox+dx, oy+dy, w, h)
    void EmitRect(float dx, float dy, float w, float h)
    {
        il.Append(il.Create(OpCodes.Ldloc_0));
        il.Append(il.Create(OpCodes.Ldc_R4, dx));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Ldloc_1));
        il.Append(il.Create(OpCodes.Ldc_R4, dy));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Ldc_R4, w));
        il.Append(il.Create(OpCodes.Ldc_R4, h));
        il.Append(il.Create(OpCodes.Newobj, rectCtor));
    }

    // Helper: emit a toggle row
    void EmitToggle(float dy, FieldDefinition field, string label)
    {
        EmitRect(10f, dy, 240f, 25f);
        il.Append(il.Create(OpCodes.Ldsfld, field));
        il.Append(il.Create(OpCodes.Ldstr, label));
        il.Append(il.Create(OpCodes.Call, guiToggle));
        il.Append(il.Create(OpCodes.Stsfld, field));
    }

    // Background box
    EmitRect(0f, 0f, 260f, 310f);
    il.Append(il.Create(OpCodes.Ldstr, "Fish Hunter Mod Menu"));
    il.Append(il.Create(OpCodes.Call, guiBox));

    // Toggle rows (dy relative to menu origin)
    float ty = 30f;
    EmitToggle(ty, toggleFields["FreePurchases"], "Free Purchases"); ty += 30f;
    EmitToggle(ty, toggleFields["NoCurrencyDeduction"], "No Currency Loss"); ty += 30f;
    EmitToggle(ty, toggleFields["BoostCurrency"], "Boost Currency (999999)"); ty += 30f;
    EmitToggle(ty, toggleFields["MaxLevel"], "Max Level"); ty += 30f;
    EmitToggle(ty, toggleFields["InfiniteAmmo"], "Infinite Ammo"); ty += 30f;
    EmitToggle(ty, toggleFields["NoAmmoCost"], "No Ammo Cost"); ty += 30f;
    EmitToggle(ty, toggleFields["InventoryMultiplier"], "3x Inventory"); ty += 30f;

    // Footer label
    EmitRect(10f, ty + 10f, 240f, 25f);
    il.Append(il.Create(OpCodes.Ldstr, "F8 = Close  |  F9 = Dev Menu"));
    il.Append(il.Create(OpCodes.Call, guiLabel));

    il.Append(ret);
    controllerType.Methods.Add(onGui);
}

module.Types.Add(controllerType);
patchCount++;
Console.WriteLine("    [OK] ModMenuController injected (F8 toggle, IMGUI menu)");

// ══════════════════════════════════════════════════════════════════════
// STEP 3: Patch F9 developer cheat menu (same as before)
// ══════════════════════════════════════════════════════════════════════
Console.WriteLine("[*] Patch: Developer cheat menu (F9 toggle)...");
PatchCheatMenu();

// ══════════════════════════════════════════════════════════════════════
// STEP 4: Conditional gameplay patches
// ══════════════════════════════════════════════════════════════════════
var currencyType = module.Types.FirstOrDefault(
    t => t.FullName == "AppRoot.Player.Currency.PlayerCurrencyProvider");

Console.WriteLine("[*] Patch: Free purchases (conditional CanSpend)...");
PatchConditionalReturnBool(currencyType, "CanSpend", toggleFields["FreePurchases"], true);

Console.WriteLine("[*] Patch: No currency deduction (conditional Spend)...");
PatchConditionalReturnVoid(currencyType, "Spend", toggleFields["NoCurrencyDeduction"]);

Console.WriteLine("[*] Patch: Boost currency (conditional Add)...");
PatchConditionalCurrencyAdd(currencyType, toggleFields["BoostCurrency"]);

Console.WriteLine("[*] Patch: Max level (conditional GetLevelForExp)...");
PatchConditionalMaxLevel(toggleFields["MaxLevel"]);

Console.WriteLine("[*] Patch: Infinite ammo (conditional GetBulletsCount)...");
PatchConditionalInfiniteAmmo(toggleFields["InfiniteAmmo"], toggleFields["NoAmmoCost"]);

Console.WriteLine("[*] Patch: 3x inventory capacity (conditional)...");
PatchConditionalInventoryCapacity(toggleFields["InventoryMultiplier"]);

// ══════════════════════════════════════════════════════════════════════
// Save
// ══════════════════════════════════════════════════════════════════════
if (patchCount > 0)
{
    string patchedPath = dllPath + ".patched";
    assembly.Write(patchedPath);
    assembly.Dispose();
    File.Delete(dllPath);
    File.Move(patchedPath, dllPath);
    Console.WriteLine($"\n[+] Done. {patchCount} patches applied.");
    Console.WriteLine("[+] In-game: press F8 for mod menu, F9 for dev cheat menu.");
    Console.WriteLine("[*] To restore: rename Assembly-CSharp.dll.backup to Assembly-CSharp.dll");
}
else
{
    assembly.Dispose();
    Console.WriteLine("\n[!] No patches applied.");
}

// ═══════════════════════════════════════════════════════════════════════
// Patch methods
// ═══════════════════════════════════════════════════════════════════════

void PatchCheatMenu()
{
    var inputType2 = module.Types.FirstOrDefault(
        t => t.FullName == "Ui.UiRoots.InputServerCheatMenu");
    var tickMethod = inputType2?.Methods.FirstOrDefault(m => m.Name == "Tick");
    if (tickMethod == null) { Console.WriteLine("    [!] InputServerCheatMenu.Tick not found"); return; }

    var uiManagerField = inputType2!.Fields.First(f => f.Name == "_uiManager");
    var uiManagerIface = uiManagerField.FieldType.Resolve()!;
    var openMethod = uiManagerIface.Methods.First(m => m.Name == "Open" && m.Parameters.Count == 1);
    var checkUiField = inputType2.Fields.First(f => f.Name == "_checkUi");
    var checkIface = checkUiField.FieldType.Resolve()!;
    var isAnyOpen = checkIface.Methods.First(m => m.Name == "get_IsAnyDialogOpen");

    var il = tickMethod.Body.GetILProcessor();
    tickMethod.Body.Instructions.Clear();

    var ret = il.Create(OpCodes.Ret);

    // Bootstrap: one-time init of ModMenuController
    var skipInit = il.Create(OpCodes.Ldc_I4, 290); // doubles as F9 keycode push
    il.Append(il.Create(OpCodes.Ldsfld, initializedField));
    il.Append(il.Create(OpCodes.Brtrue, skipInit));
    il.Append(il.Create(OpCodes.Ldc_I4_1));
    il.Append(il.Create(OpCodes.Stsfld, initializedField));
    var initMethod = controllerType.Methods.First(m => m.Name == "Init");
    il.Append(il.Create(OpCodes.Call, initMethod));

    // F9 cheat menu check (skipInit lands here)
    il.Append(skipInit); // Ldc_I4 290 = KeyCode.F9
    il.Append(il.Create(OpCodes.Call, getKeyDown));
    il.Append(il.Create(OpCodes.Brfalse, ret));

    il.Append(il.Create(OpCodes.Ldc_I4_0));
    il.Append(il.Create(OpCodes.Call, setLockState));
    il.Append(il.Create(OpCodes.Ldc_I4_1));
    il.Append(il.Create(OpCodes.Call, setVisible));

    il.Append(il.Create(OpCodes.Ldarg_0));
    il.Append(il.Create(OpCodes.Ldfld, checkUiField));
    il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(isAnyOpen)));
    il.Append(il.Create(OpCodes.Brtrue, ret));

    il.Append(il.Create(OpCodes.Ldarg_0));
    il.Append(il.Create(OpCodes.Ldfld, uiManagerField));
    il.Append(il.Create(OpCodes.Ldc_I4, 1001));
    il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(openMethod)));
    il.Append(ret);

    patchCount++;
    Console.WriteLine("    [OK] F9 toggles cheat menu with cursor unlock");

    // Patch OnHide to re-lock cursor
    var cheatMenuType = module.Types.FirstOrDefault(t => t.FullName == "CheatMenuScreen");
    var onHide = cheatMenuType?.Methods.FirstOrDefault(m => m.Name == "OnHide");
    if (onHide != null)
    {
        var hideIl = onHide.Body.GetILProcessor();
        var first = onHide.Body.Instructions[0];
        hideIl.InsertBefore(first, hideIl.Create(OpCodes.Ldc_I4_1));
        hideIl.InsertBefore(first, hideIl.Create(OpCodes.Call, setLockState));
        hideIl.InsertBefore(first, hideIl.Create(OpCodes.Ldc_I4_0));
        hideIl.InsertBefore(first, hideIl.Create(OpCodes.Call, setVisible));
        patchCount++;
        Console.WriteLine("    [OK] OnHide re-locks cursor");
    }
}

/// Insert at method start: if (toggle) return <value>;
void PatchConditionalReturnBool(TypeDefinition? type, string methodName,
    FieldDefinition toggleField, bool returnValue)
{
    var method = type?.Methods.FirstOrDefault(m => m.Name == methodName);
    if (method == null) { Console.WriteLine($"    [!] {methodName} not found"); return; }

    var il = method.Body.GetILProcessor();
    var originalFirst = method.Body.Instructions[0];

    il.InsertBefore(originalFirst, il.Create(OpCodes.Ldsfld, toggleField));
    il.InsertBefore(originalFirst, il.Create(OpCodes.Brfalse, originalFirst));
    il.InsertBefore(originalFirst, il.Create(returnValue ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
    il.InsertBefore(originalFirst, il.Create(OpCodes.Ret));

    patchCount++;
    Console.WriteLine($"    [OK] {methodName} conditionally returns {returnValue}");
}

/// Insert at method start: if (toggle) return;
void PatchConditionalReturnVoid(TypeDefinition? type, string methodName,
    FieldDefinition toggleField)
{
    var method = type?.Methods.FirstOrDefault(m => m.Name == methodName);
    if (method == null) { Console.WriteLine($"    [!] {methodName} not found"); return; }

    var il = method.Body.GetILProcessor();
    var originalFirst = method.Body.Instructions[0];

    il.InsertBefore(originalFirst, il.Create(OpCodes.Ldsfld, toggleField));
    il.InsertBefore(originalFirst, il.Create(OpCodes.Brfalse, originalFirst));
    il.InsertBefore(originalFirst, il.Create(OpCodes.Ret));

    patchCount++;
    Console.WriteLine($"    [OK] {methodName} conditionally skipped");
}

/// Insert at method start: if (toggle) amount = 999999;
void PatchConditionalCurrencyAdd(TypeDefinition? type, FieldDefinition toggleField)
{
    var method = type?.Methods.FirstOrDefault(m => m.Name == "Add");
    if (method == null) { Console.WriteLine("    [!] Add not found"); return; }

    var il = method.Body.GetILProcessor();
    var originalFirst = method.Body.Instructions[0];

    il.InsertBefore(originalFirst, il.Create(OpCodes.Ldsfld, toggleField));
    il.InsertBefore(originalFirst, il.Create(OpCodes.Brfalse, originalFirst));
    il.InsertBefore(originalFirst, il.Create(OpCodes.Ldc_I4, 999999));
    il.InsertBefore(originalFirst, il.Create(OpCodes.Starg, method.Parameters[1]));

    patchCount++;
    Console.WriteLine("    [OK] Add conditionally boosts to 999999");
}

/// Insert at method start: if (toggle) return _expToLevel[last].Level;
void PatchConditionalMaxLevel(FieldDefinition toggleField)
{
    var type = module.Types.FirstOrDefault(t => t.FullName == "Features.Levels.LevelsConfig");
    var method = type?.Methods.FirstOrDefault(m => m.Name == "GetLevelForExp");
    if (method == null) { Console.WriteLine("    [!] GetLevelForExp not found"); return; }

    var expToLevelField = type!.Fields.FirstOrDefault(f => f.Name == "_expToLevel");
    var elemType = expToLevelField!.FieldType.GetElementType().Resolve();
    var levelField = elemType.Fields.FirstOrDefault(f => f.Name == "Level");

    var il = method.Body.GetILProcessor();
    var originalFirst = method.Body.Instructions[0];

    il.InsertBefore(originalFirst, il.Create(OpCodes.Ldsfld, toggleField));
    il.InsertBefore(originalFirst, il.Create(OpCodes.Brfalse, originalFirst));
    il.InsertBefore(originalFirst, il.Create(OpCodes.Ldarg_0));
    il.InsertBefore(originalFirst, il.Create(OpCodes.Ldfld, expToLevelField));
    il.InsertBefore(originalFirst, il.Create(OpCodes.Ldarg_0));
    il.InsertBefore(originalFirst, il.Create(OpCodes.Ldfld, expToLevelField));
    il.InsertBefore(originalFirst, il.Create(OpCodes.Ldlen));
    il.InsertBefore(originalFirst, il.Create(OpCodes.Conv_I4));
    il.InsertBefore(originalFirst, il.Create(OpCodes.Ldc_I4_1));
    il.InsertBefore(originalFirst, il.Create(OpCodes.Sub));
    il.InsertBefore(originalFirst, il.Create(OpCodes.Ldelem_Ref));
    il.InsertBefore(originalFirst, il.Create(OpCodes.Ldfld, module.ImportReference(levelField)));
    il.InsertBefore(originalFirst, il.Create(OpCodes.Ret));

    patchCount++;
    Console.WriteLine("    [OK] GetLevelForExp conditionally returns max level");
}

void PatchConditionalInfiniteAmmo(FieldDefinition ammoToggle, FieldDefinition costToggle)
{
    var bulletsType = module.Types.FirstOrDefault(
        t => t.FullName == "Features.Shooting.MyBulletsInventory");
    if (bulletsType == null) { Console.WriteLine("    [!] MyBulletsInventory not found"); return; }

    // GetBulletsCount: if (toggle) return 999;
    var getCount = bulletsType.Methods.FirstOrDefault(
        m => m.Name == "GetBulletsCount" && m.Parameters.Count == 1);
    if (getCount != null)
    {
        var il = getCount.Body.GetILProcessor();
        var originalFirst = getCount.Body.Instructions[0];

        il.InsertBefore(originalFirst, il.Create(OpCodes.Ldsfld, ammoToggle));
        il.InsertBefore(originalFirst, il.Create(OpCodes.Brfalse, originalFirst));
        il.InsertBefore(originalFirst, il.Create(OpCodes.Ldc_I4, 999));
        il.InsertBefore(originalFirst, il.Create(OpCodes.Ret));

        patchCount++;
        Console.WriteLine("    [OK] GetBulletsCount conditionally returns 999");
    }

    // CostBullets: if (toggle) return;
    var cost = bulletsType.Methods.FirstOrDefault(m => m.Name == "CostBullets");
    if (cost != null)
    {
        var il = cost.Body.GetILProcessor();
        var originalFirst = cost.Body.Instructions[0];

        il.InsertBefore(originalFirst, il.Create(OpCodes.Ldsfld, costToggle));
        il.InsertBefore(originalFirst, il.Create(OpCodes.Brfalse, originalFirst));
        il.InsertBefore(originalFirst, il.Create(OpCodes.Ret));

        patchCount++;
        Console.WriteLine("    [OK] CostBullets conditionally skipped");
    }
}

void PatchConditionalInventoryCapacity(FieldDefinition toggleField)
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
            var next = instructions[i + 1]; // instruction after Ldfld Capacity

            // Insert: if (toggle) { ldc.i4.3; mul; }
            var load3 = il.Create(OpCodes.Ldc_I4_3);
            var mul = il.Create(OpCodes.Mul);

            il.InsertBefore(next, il.Create(OpCodes.Ldsfld, toggleField));
            il.InsertBefore(next, il.Create(OpCodes.Brfalse, next));
            il.InsertBefore(next, load3);
            il.InsertBefore(next, mul);

            patchCount++;
            Console.WriteLine("    [OK] Capacity conditionally multiplied by 3");
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
