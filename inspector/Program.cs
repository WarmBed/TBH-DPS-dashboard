using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

class Program
{
    static MetadataLoadContext mlc;

    static int Main(string[] args)
    {
        string interop = @"D:\SteamLibrary\steamapps\common\TaskbarHero\BepInEx\interop";
        var dlls = Directory.GetFiles(interop, "*.dll").ToList();
        string bepCore = @"D:\SteamLibrary\steamapps\common\TaskbarHero\BepInEx\core";
        if (Directory.Exists(bepCore)) dlls.AddRange(Directory.GetFiles(bepCore, "*.dll"));
        // include the runtime dir core libs so MetadataLoadContext can resolve System.*
        string coreDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        dlls.AddRange(Directory.GetFiles(coreDir, "*.dll"));
        // dedupe by filename (interop wins)
        dlls = dlls.GroupBy(p => Path.GetFileName(p).ToLowerInvariant()).Select(g => g.First()).ToList();
        var resolver = new PathAssemblyResolver(dlls);
        mlc = new MetadataLoadContext(resolver, coreAssemblyName: "System.Private.CoreLib");

        var asmCs = mlc.LoadFromAssemblyPath(Path.Combine(interop, "Assembly-CSharp.dll"));
        var asmFp = mlc.LoadFromAssemblyPath(Path.Combine(interop, "Assembly-CSharp-firstpass.dll"));
        var allTypes = new List<Type>();
        foreach (var a in new[] { asmCs, asmFp })
        {
            try { allTypes.AddRange(a.GetTypes()); }
            catch (ReflectionTypeLoadException ex) { allTypes.AddRange(ex.Types.Where(t => t != null)); }
        }

        string mode = args.Length > 0 ? args[0] : "search";
        if (mode == "type")
        {
            foreach (var name in args.Skip(1))
            {
                var t = allTypes.FirstOrDefault(x => x.FullName == name || x.Name == name);
                if (t == null) { Console.WriteLine($"### NOT FOUND: {name}"); continue; }
                DumpType(t);
            }
        }
        else if (mode == "param")
        {
            // find methods that take a parameter whose type name contains keyword
            var kw = args[1].ToLowerInvariant();
            foreach (var t in allTypes)
            {
                MethodInfo[] ms;
                try { ms = t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly); }
                catch { continue; }
                foreach (var m in ms)
                {
                    try {
                        if (m.GetParameters().Any(p => (p.ParameterType.Name ?? "").ToLowerInvariant().Contains(kw)))
                            Console.WriteLine($"  {t.FullName}::{Sig(m)}");
                    } catch { }
                }
            }
        }
        else if (mode == "find")
        {
            // find types/methods whose name contains any keyword
            var kws = args.Skip(1).Select(s => s.ToLowerInvariant()).ToArray();
            foreach (var t in allTypes)
            {
                if (kws.Any(k => (t.Name ?? "").ToLowerInvariant().Contains(k)))
                    Console.WriteLine($"TYPE  {t.FullName}");
            }
            Console.WriteLine("--- methods matching ---");
            foreach (var t in allTypes)
            {
                MethodInfo[] ms;
                try { ms = t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly); }
                catch { continue; }
                foreach (var m in ms)
                    if (kws.Any(k => m.Name.ToLowerInvariant().Contains(k)))
                        Console.WriteLine($"  {t.FullName}::{Sig(m)}");
            }
        }
        else if (mode == "sig")
        {
            // find methods by SIGNATURE: sig <retTypeName> [paramTypeName ...]  ('*' = any)
            // matches by simple type-name (case-insensitive). Use to relocate obfuscated helpers after
            // a game update when both type and method names churn (e.g. gold/item/box static helpers).
            string ret = args.Length > 1 ? args[1].ToLowerInvariant() : "*";
            var ptypes = args.Skip(2).Select(s => s.ToLowerInvariant()).ToArray();
            foreach (var t in allTypes)
            {
                MethodInfo[] ms;
                try { ms = t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly); }
                catch { continue; }
                foreach (var m in ms)
                {
                    var ps = m.GetParameters();
                    if (ps.Length != ptypes.Length) continue;
                    if (ret != "*" && (m.ReturnType.Name ?? "").ToLowerInvariant() != ret) continue;
                    bool ok = true;
                    for (int i = 0; i < ptypes.Length; i++)
                        if (ptypes[i] != "*" && (ps[i].ParameterType.Name ?? "").ToLowerInvariant() != ptypes[i]) { ok = false; break; }
                    if (ok) Console.WriteLine($"  {t.FullName}::{Sig(m)}{(m.IsStatic?"  [static]":"")}");
                }
            }
        }
        return 0;
    }

    static void DumpType(Type t)
    {
        Console.WriteLine($"\n================ {t.FullName}  ({(t.IsEnum?"enum":t.IsValueType?"struct":t.IsInterface?"interface":"class")}) ================");
        try { if (t.BaseType != null) Console.WriteLine($"  base: {t.BaseType.FullName}"); } catch { }
        try { var ifs = t.GetInterfaces(); if (ifs.Length > 0) Console.WriteLine($"  implements: {string.Join(", ", ifs.Select(i=>i.Name))}"); } catch { }
        if (t.IsEnum)
        {
            foreach (var f in t.GetFields(BindingFlags.Public|BindingFlags.Static))
                Console.WriteLine($"  {f.Name} = {f.GetRawConstantValue()}");
            return;
        }
        var flags = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly;
        Console.WriteLine("  -- fields --");
        try { foreach (var f in t.GetFields(flags))
            try { Console.WriteLine($"    {(f.IsStatic?"static ":"")}{(f.IsPublic?"pub ":"prv ")}{TN(f.FieldType)} {f.Name}"); } catch { } } catch { }
        Console.WriteLine("  -- properties --");
        try { foreach (var p in t.GetProperties(flags))
        {
            try { string acc = (p.CanRead ? "get;" : "") + (p.CanWrite ? "set;" : "");
            Console.WriteLine("    " + TN(p.PropertyType) + " " + p.Name + " {" + acc + "}"); } catch { }
        } } catch { }
        Console.WriteLine("  -- methods --");
        try { foreach (var m in t.GetMethods(flags))
            try { Console.WriteLine($"    {(m.IsStatic?"static ":"")}{(m.IsPublic?"pub ":"prv ")}{Sig(m)}"); } catch { } } catch { }
    }

    static string Sig(MethodInfo m)
        => $"{TN(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(p => TN(p.ParameterType)+" "+p.Name))})";

    static string TN(Type t)
    {
        if (t == null) return "?";
        if (t.IsGenericType)
        {
            var n = t.Name; int tick = n.IndexOf('`');
            if (tick >= 0) n = n.Substring(0, tick);
            return n + "<" + string.Join(", ", t.GetGenericArguments().Select(TN)) + ">";
        }
        return t.Name;
    }
}
