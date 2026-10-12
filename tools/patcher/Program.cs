// Patches the retail R.E.P.O. managed assemblies so they can be AOT-compiled with IL2CPP and run on iOS.
//
//   RepoPatcher patch   <retailManagedDir> <RepoShim.dll> <outDir>
//   RepoPatcher analyze <managedDir>
//
// All shipped assemblies:
//  * every P/Invoke (Steam, Discord, Photon Voice natives, user32, ...) becomes a managed stub. Stubs throw
//    DllNotFoundException (callers like SteamClient.Init already expect that), except user32 which just
//    returns the default value.
//  * Process.Start goes to RepoShim.Platform (http(s) URLs open in Safari, everything else is blocked).
//  * UnityEngine.Microphone goes to RepoShim.Mic (no microphone: no permission prompt, no recording).
// Input-reading assemblies (game, UnityEngine.UI, TextMeshPro, ...):
//  * every legacy UnityEngine.Input member goes to RepoShim.VInput (virtual mouse/keys driven by touch).
// Assembly-CSharp:
//  * Cursor.lockState / visible setters go through RepoShim.IosCursor (the touch layer reads them).
//  * Environment.MachineName (the offline player name) -> RepoShim.Platform.PlayerName.
//  * vSyncCount / targetFrameRate go through RepoShim.Platform (iOS needs an explicit frame rate).
//  * Screen.SetResolution is ignored (the game re-applies the desktop resolution).
//  * DiscordManager does nothing (no Discord SDK on iOS).
// Unity.Postprocessing.Runtime:
//  * compute shaders are reported unsupported (auto exposure, MSVO and the 3D LUT baker are compute-only).
using Mono.Cecil;
using Mono.Cecil.Cil;

static class Program
{
    // Assemblies that come from the iOS player itself (engine modules, BCL) and are not shipped as plugins.
    static bool IsEngineOrBcl(string file)
    {
        string n = Path.GetFileNameWithoutExtension(file);
        if (n == "UnityEngine.UI") return false;   // a package, shipped like the game's own assemblies
        return n == "UnityEngine" || n.StartsWith("UnityEngine.") || n.StartsWith("System") || n.StartsWith("Mono.") ||
               n == "mscorlib" || n == "netstandard" || n.StartsWith("Microsoft.");
    }

    // Assemblies whose legacy UnityEngine.Input reads are redirected to the virtual input.
    static readonly HashSet<string> InputRedirect = new HashSet<string>
    {
        "Assembly-CSharp", "Assembly-CSharp-firstpass", "UnityEngine.UI", "Unity.TextMeshPro",
        "PhotonUnityNetworking.Utilities", "PhotonVoice", "Unity.VisualScripting.Flow", "Unity.RenderPipelines.Core.Runtime",
    };

    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "analyze") return Analyze.Run(args[1]);
        if (args.Length >= 2 && args[0] == "stackcheck") return Analyze.StackCheck(args.Skip(1).ToArray());
        if (args.Length >= 3 && args[0] == "dumpil") return Analyze.DumpIL(args[1], args[2]);
        if (args.Length >= 3 && args[0] == "types") return Analyze.Types(args[1], args[2]);
        if (args.Length >= 3 && args[0] == "memberrefs") return Analyze.MemberRefs(args[1], args[2]);
        if (args.Length >= 3 && args[0] == "calls") return Analyze.Calls(args[1], args[2]);
        if (args.Length >= 4 && args[0] == "patch") return Patch(args[1], args[2], args[3]);
        if (args.Length >= 3 && args[0] == "publicize") return Publicize(args[1], args[2]);
        Console.Error.WriteLine("usage: RepoPatcher patch <managedDir> <RepoShim.dll> <outDir> | analyze <managedDir>");
        return 1;
    }

    static int Patch(string inDir, string shimPath, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(inDir);
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(shimPath)));
        var rp = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate, ReadSymbols = false };
        var shim = AssemblyDefinition.ReadAssembly(shimPath, rp);

        var shipped = Directory.GetFiles(inDir, "*.dll").Where(f => !IsEngineOrBcl(f)).OrderBy(f => f).ToList();
        int total = 0, failures = 0;
        foreach (var path in shipped)
        {
            string name = Path.GetFileName(path);
            var asm = AssemblyDefinition.ReadAssembly(path, rp);
            var ctx = new Ctx(asm.MainModule, shim.MainModule);
            int n = StubPInvokes(ctx);
            n += RedirectProcess(ctx);
            n += RedirectMicrophone(ctx);
            if (InputRedirect.Contains(asm.Name.Name))
                n += RedirectInput(ctx);
            if (asm.Name.Name == "Assembly-CSharp")
                n += PatchGame(ctx);
            if (asm.Name.Name == "Unity.Postprocessing.Runtime")
                n += NoComputeShaders(ctx);
            total += n;
            failures += ctx.Failures;
            asm.Write(Path.Combine(outDir, name));
            if (n > 0 || ctx.Failures > 0)
                Console.WriteLine($"{name}: {n} patches{(ctx.Failures > 0 ? $", {ctx.Failures} FAILED" : "")}");
        }
        File.Copy(shimPath, Path.Combine(outDir, "RepoShim.dll"), true);
        Console.WriteLine($"{shipped.Count} assemblies, total {total} patches, {failures} failures");
        return failures == 0 ? 0 : 2;
    }

    // Compile-time reference copy with every type/member public, so the iOS mod can read the game's internal
    // state (IL2CPP, like Mono, does not enforce member access at runtime). Never shipped.
    static int Publicize(string inPath, string outPath)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(inPath)));
        var asm = AssemblyDefinition.ReadAssembly(inPath, new ReaderParameters { AssemblyResolver = resolver });
        foreach (var t in asm.MainModule.GetTypes())
        {
            if (t.IsNested) t.IsNestedPublic = true; else t.IsPublic = true;
            foreach (var m in t.Methods) m.IsPublic = true;
            foreach (var f in t.Fields)
                if (!t.Events.Any(e => e.Name == f.Name)) f.IsPublic = true;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
        asm.Write(outPath);
        return 0;
    }

    sealed class Ctx
    {
        public readonly ModuleDefinition Mod;
        public readonly ModuleDefinition Shim;
        public int Failures;
        public Ctx(ModuleDefinition mod, ModuleDefinition shim) { Mod = mod; Shim = shim; }

        public TypeDefinition ShimType(string name) => Shim.GetType("RepoShim." + name) ?? throw new Exception("shim type missing: " + name);

        public MethodReference ShimMethod(string type, string name, params string[] paramTypes)
        {
            var m = ShimType(type).Methods.FirstOrDefault(x => x.Name == name && x.Parameters.Count == paramTypes.Length &&
                x.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(paramTypes));
            if (m == null) throw new Exception($"shim method missing: {type}.{name}({string.Join(",", paramTypes)})");
            return Mod.ImportReference(m);
        }

        public void Fail(string what)
        {
            Console.WriteLine("  !! " + what);
            Failures++;
        }
    }

    static IEnumerable<(MethodDefinition m, Instruction ins)> Calls(ModuleDefinition mod)
    {
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions.ToList())
                    if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && ins.Operand is MethodReference)
                        yield return (m, ins);
            }
    }

    static string Sig(MethodReference mr) => string.Join(",", mr.Parameters.Select(p => p.ParameterType.FullName));

    // ------------------------------------------------------------------------------------------
    static int StubPInvokes(Ctx c)
    {
        int n = 0;
        var mod = c.Mod;
        var dnfType = new TypeReference("System", "DllNotFoundException", mod, mod.TypeSystem.CoreLibrary);
        var dnfCtor = new MethodReference(".ctor", mod.TypeSystem.Void, dnfType) { HasThis = true };
        dnfCtor.Parameters.Add(new ParameterDefinition(mod.TypeSystem.String));
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.IsPInvokeImpl || m.PInvokeInfo == null) continue;
                string lib = m.PInvokeInfo.Module.Name;
                m.PInvokeInfo = null;     // Cecil's setter turns IsPInvokeImpl back on,
                m.IsPInvokeImpl = false;  // so clear the flag afterwards
                m.IsPreserveSig = false;
                m.ImplAttributes &= ~MethodImplAttributes.PreserveSig;
                m.ImplAttributes |= MethodImplAttributes.IL | MethodImplAttributes.Managed;
                m.Body = new MethodBody(m);
                var il = m.Body.GetILProcessor();
                if (lib.StartsWith("user32", StringComparison.OrdinalIgnoreCase))
                {
                    // window title cosmetics: just return default(T)
                    EmitDefaultReturn(m, il);
                }
                else
                {
                    il.Emit(OpCodes.Ldstr, lib + " is not available on iOS");
                    il.Emit(OpCodes.Newobj, dnfCtor);
                    il.Emit(OpCodes.Throw);
                }
                n++;
            }
        return n;
    }

    static void EmitDefaultReturn(MethodDefinition m, ILProcessor il)
    {
        foreach (var p in m.Parameters)
            if (p.IsOut && p.ParameterType is ByReferenceType br)
            {
                il.Emit(OpCodes.Ldarg, p);
                il.Emit(OpCodes.Initobj, br.ElementType);
            }
        var rt = m.ReturnType;
        if (rt.FullName != "System.Void")
        {
            var v = new VariableDefinition(rt);
            m.Body.Variables.Add(v);
            m.Body.InitLocals = true;
            il.Emit(OpCodes.Ldloca, v);
            il.Emit(OpCodes.Initobj, rt);
            il.Emit(OpCodes.Ldloc, v);
        }
        il.Emit(OpCodes.Ret);
    }

    // ------------------------------------------------------------------------------------------
    static int RedirectProcess(Ctx c)
    {
        int n = 0;
        foreach (var (m, ins) in Calls(c.Mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName != "System.Diagnostics.Process") continue;
            if (mr.Name == "Start" && !mr.HasThis && Sig(mr) == "System.String")
            {
                ins.OpCode = OpCodes.Call;
                ins.Operand = c.ShimMethod("Platform", "ProcessStart", "System.String");
                n++;
            }
            else if (mr.Name == "Start" || mr.Name.StartsWith("GetProcesses"))
                c.Fail($"{m.FullName}: unhandled Process.{mr.Name}({Sig(mr)})");
        }
        return n;
    }

    // ------------------------------------------------------------------------------------------
    static int RedirectMicrophone(Ctx c)
    {
        int n = 0;
        TypeDefinition mic = null;
        foreach (var (m, ins) in Calls(c.Mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName != "UnityEngine.Microphone") continue;
            mic ??= c.ShimType("Mic");
            var target = mic.Methods.FirstOrDefault(x => x.Name == mr.Name && x.IsStatic && Sig(x) == Sig(mr));
            if (target == null)
            {
                c.Fail($"{m.FullName}: no Mic equivalent for Microphone.{mr.Name}({Sig(mr)})");
                continue;
            }
            ins.OpCode = OpCodes.Call;
            ins.Operand = c.Mod.ImportReference(target);
            n++;
        }
        return n;
    }

    // ------------------------------------------------------------------------------------------
    static int RedirectInput(Ctx c)
    {
        int n = 0;
        var vinput = c.ShimType("VInput");
        foreach (var (m, ins) in Calls(c.Mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName != "UnityEngine.Input") continue;
            var target = vinput.Methods.FirstOrDefault(x => x.Name == mr.Name && x.IsStatic &&
                x.Parameters.Count == mr.Parameters.Count && Sig(x) == Sig(mr) && x.ReturnType.FullName == mr.ReturnType.FullName);
            if (target == null)
            {
                c.Fail($"{m.FullName}: no VInput equivalent for Input.{mr.Name}({Sig(mr)})");
                continue;
            }
            ins.OpCode = OpCodes.Call;
            ins.Operand = c.Mod.ImportReference(target);
            n++;
        }
        return n;
    }

    // ------------------------------------------------------------------------------------------
    static int PatchGame(Ctx c)
    {
        var mod = c.Mod;
        int n = 0;

        var setVSync = c.ShimMethod("Platform", "SetVSyncCount", "System.Int32");
        var setFps = c.ShimMethod("Platform", "SetTargetFrameRate", "System.Int32");
        var setRes = c.ShimMethod("Platform", "SetResolution", "System.Int32", "System.Int32", "System.Boolean");
        var playerName = c.Mod.ImportReference(c.ShimType("Platform").Methods.First(x => x.Name == "get_PlayerName"));
        var cursor = c.ShimType("IosCursor");
        var setLock = c.Mod.ImportReference(cursor.Methods.First(x => x.Name == "SetLockState"));
        var setVisible = c.Mod.ImportReference(cursor.Methods.First(x => x.Name == "SetVisible"));
        int fps = 0, names = 0, cursors = 0;
        foreach (var (m, ins) in Calls(mod))
        {
            var mr = (MethodReference)ins.Operand;
            string dt = mr.DeclaringType.FullName;
            MethodReference to = null;
            if (dt == "UnityEngine.QualitySettings" && mr.Name == "set_vSyncCount") { to = setVSync; fps++; }
            else if (dt == "UnityEngine.Application" && mr.Name == "set_targetFrameRate") { to = setFps; fps++; }
            else if (dt == "UnityEngine.Screen" && mr.Name == "SetResolution" && Sig(mr) == "System.Int32,System.Int32,System.Boolean") to = setRes;
            else if (dt == "System.Environment" && mr.Name == "get_MachineName") { to = playerName; names++; }
            else if (dt == "UnityEngine.Cursor" && mr.Name == "set_lockState") { to = setLock; cursors++; }
            else if (dt == "UnityEngine.Cursor" && mr.Name == "set_visible") { to = setVisible; cursors++; }
            if (to == null) continue;
            ins.OpCode = OpCodes.Call;
            ins.Operand = to;
            n++;
        }
        if (fps < 3) c.Fail($"expected >= 3 frame-rate setters, found {fps}");
        if (names < 1) c.Fail("Environment.MachineName (player name) not found");
        if (cursors < 2) c.Fail($"expected Cursor setters, found {cursors}");

        // --- Discord: no SDK on iOS. Start() would throw creating the native client, and every later rich
        //     presence update would hit a null activity. Keep Awake (singleton), make the rest no-ops.
        var discord = mod.GetType("DiscordManager");
        if (discord == null) c.Fail("DiscordManager missing");
        else
            foreach (var m in discord.Methods)
            {
                if (!m.HasBody || m.IsConstructor || m.Name == "Awake" || m.ReturnType.FullName != "System.Void") continue;
                m.Body = new MethodBody(m);
                m.Body.GetILProcessor().Emit(OpCodes.Ret);
                n++;
            }
        return n;
    }

    // Post-processing compute shaders (auto exposure histogram, 3D LUT baker, MSVO ambient occlusion) are
    // DX11 bytecode with no Metal translation. With compute reported unsupported, the post-processing stack
    // skips auto exposure / MSVO and uses its pixel-shader paths (2D LUT colour grading) instead.
    static int NoComputeShaders(Ctx c)
    {
        int n = 0;
        foreach (var (m, ins) in Calls(c.Mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName == "UnityEngine.SystemInfo" && mr.Name == "get_supportsComputeShaders")
            {
                ins.OpCode = OpCodes.Ldc_I4_0;
                ins.Operand = null;
                n++;
            }
        }
        if (n == 0) c.Fail("no SystemInfo.supportsComputeShaders reads found");
        return n;
    }
}
