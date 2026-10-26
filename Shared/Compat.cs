// Compat.cs — проверка совместимости модов до запуска игры (только чтение файлов, сборки не загружаются).
// Манифест мода — атрибуты MelonLoader в DLL: MelonInfo, MelonGame, MelonGameVersion, VerifyLoaderVersion,
// MelonPlatform(Domain), MelonProcess, MelonAdditional/Optional dependencies, MelonIncompatibleAssemblies,
// плюс ссылки на сборки (MelonLoader, Il2CppInterop, 0Harmony, .NET) и цели [HarmonyPatch].
// Окружение — MelonLoader (0.6.x / 0.7.x Open-Beta), Unity, .NET-рантайм MelonLoader, сборки игры (Il2CppAssemblies),
// библиотеки (UserLibs), MelonPreferences.cfg. Итог — понятные метки и объяснения для карточек и проверка перед «Играть».
#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using IOPath = System.IO.Path;

namespace SR2ModManager.Core;

public enum CompatLevel { Ok = 0, Info = 1, Warning = 2, Error = 3 }

public sealed class CompatIssue
{
    public CompatLevel Level;
    public string Code = "";              // машинный код: loader-newer, missing-lib, hook-conflict…
    public string Badge = "";             // короткая метка на карточке
    public string Title = "";             // заголовок в подробностях
    public string Text = "";              // что не так и почему
    public string Fix = "";               // что сделать
    public List<string> Related = new();  // Id других модов (дубликат, конфликт)
    public override string ToString() => $"[{Level}] {Code}: {Title} — {Text}" + (Fix.Length > 0 ? " → " + Fix : "");
}

/// Метод игры, который мод патчит через Harmony.
public sealed class HookTarget
{
    public string Type = "";        // полное имя типа без сборки
    public string Method = "";      // Start, get_Value, .ctor; "?" — цель вычисляется в коде (TargetMethod)
    public string Kind = "";        // Prefix / Postfix / Transpiler / Finalizer
    public bool SkipsOriginal;      // префикс возвращает bool — может отменить оригинал (и чужие патчи)
    public string PatchClass = "";
    public bool Manual;             // найден в коде: harmony.Patch(AccessTools.Method(typeof(X), "Y"), …)
    public bool Dynamic => Method == "?" || Type.Length == 0;
    public string Key => Type + "::" + Method;
    public string Short => ShortType(Type) + "." + Method;
    public static string ShortType(string t)
    {
        int i = Math.Max(t.LastIndexOf('.'), t.LastIndexOf('+'));
        return i >= 0 ? t.Substring(i + 1) : t;
    }
    public string KindText => Kind switch
    {
        "Prefix" => SkipsOriginal ? "префикс с отменой" : "префикс",
        "Postfix" => "постфикс",
        "Transpiler" => "транспайлер",
        "Finalizer" => "финализатор",
        "Patch" => "патч в коде",
        _ => Kind,
    };
}

/// Что известно о моде из метаданных DLL.
public sealed class ModManifest
{
    public string Path = "";
    public bool Readable;
    public string Error = "";
    public string AssemblyName = "";
    public Version? AssemblyVersion;
    public bool IsMelon;
    public bool LegacyInfo;                       // MelonModInfo / MelonPluginInfo — MelonLoader 0.2–0.3
    public string MelonKind = "", MelonName = "", MelonVersion = "", MelonAuthor = "";
    public string TargetFramework = "";           // ".NETCoreApp,Version=v6.0"
    public Version? LoaderRef;                    // версия MelonLoader.dll, против которой собран мод
    public string RequiredLoader = "";            // VerifyLoaderVersion
    public bool RequiredIsMinimum;
    public List<string> GameVersions = new();     // MelonGameVersion
    public List<(string Developer, string Name)> Games = new(); // MelonGame
    public int Domain = -1;                       // MelonPlatformDomain: 0 любая, 1 Mono, 2 Il2Cpp
    public List<int> Platforms = new();           // MelonPlatform: 0 любая, 1 Windows x86, 2 Windows x64
    public List<string> Processes = new();        // MelonProcess
    public List<string> Additional = new();       // MelonAdditionalDependencies
    public List<string> Optional = new();         // MelonOptionalDependencies
    public List<string> Incompatible = new();     // MelonIncompatibleAssemblies
    public int Priority;
    public Dictionary<string, Version> References = new(StringComparer.OrdinalIgnoreCase);
    public bool UsesHarmony, UsesUnhollower, UsesIl2CppInterop, UsesPreferences, ManualPatching, DontPatchAll;
    public List<HookTarget> Hooks = new();

    public string FrameworkKind
    {
        get
        {
            var t = TargetFramework;
            if (t.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase)) return "core";
            if (t.StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase)) return "framework";
            if (t.StartsWith(".NETStandard", StringComparison.OrdinalIgnoreCase)) return "standard";
            return "";
        }
    }

    public Version? FrameworkVersion
    {
        get
        {
            var m = Regex.Match(TargetFramework, @"Version=v(\d+(?:\.\d+){0,3})", RegexOptions.IgnoreCase);
            return m.Success && Version.TryParse(m.Groups[1].Value.Contains('.') ? m.Groups[1].Value : m.Groups[1].Value + ".0", out var v) ? v : null;
        }
    }

    public string FrameworkLabel => FrameworkKind switch
    {
        "core" => ".NET " + FrameworkVersion?.ToString(2),
        "framework" => ".NET Framework " + FrameworkVersion,
        "standard" => ".NET Standard " + FrameworkVersion?.ToString(2),
        _ => "",
    };
}

/// Чтение манифестов из DLL (System.Reflection.Metadata, без загрузки сборки). Кэш по пути, размеру и времени файла.
public static class ManifestReader
{
    private static readonly Dictionary<string, (long Len, long Ticks, ModManifest M)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (long Len, long Ticks, Version? V)> Versions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static ModManifest Read(string path)
    {
        FileInfo fi;
        try { fi = new FileInfo(path); }
        catch (Exception e) { return new ModManifest { Path = path, Error = e.Message }; }
        if (!fi.Exists) return new ModManifest { Path = path, Error = "файл не найден" };
        long len = fi.Length, ticks = fi.LastWriteTimeUtc.Ticks;
        lock (Gate)
            if (Cache.TryGetValue(fi.FullName, out var hit) && hit.Len == len && hit.Ticks == ticks) return hit.M;
        var m = new ModManifest { Path = fi.FullName };
        try
        {
            using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            ReadFrom(fs, m);
        }
        catch (Exception e) { m.Readable = false; m.Error = e.Message; }
        lock (Gate)
        {
            if (Cache.Count > 1000) Cache.Clear();
            Cache[fi.FullName] = (len, ticks, m);
        }
        return m;
    }

    /// Манифест из кэша, если файл не менялся (без чтения DLL — только размер и время).
    public static ModManifest? Cached(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            lock (Gate)
                return Cache.TryGetValue(fi.FullName, out var hit) && hit.Len == fi.Length && hit.Ticks == fi.LastWriteTimeUtc.Ticks ? hit.M : null;
        }
        catch { return null; }
    }

    public static ModManifest Read(Stream s, string path)
    {
        var m = new ModManifest { Path = path };
        try { ReadFrom(s, m); } catch (Exception e) { m.Readable = false; m.Error = e.Message; }
        return m;
    }

    /// Версия сборки (AssemblyVersion) без загрузки; null — не .NET-сборка.
    public static Version? AssemblyVersionOf(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            lock (Gate)
                if (Versions.TryGetValue(fi.FullName, out var hit) && hit.Len == fi.Length && hit.Ticks == fi.LastWriteTimeUtc.Ticks) return hit.V;
            Version? v = null;
            using (var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var pe = new PEReader(fs))
                if (pe.HasMetadata)
                {
                    var md = pe.GetMetadataReader();
                    if (md.IsAssembly) v = md.GetAssemblyDefinition().Version;
                }
            lock (Gate)
            {
                if (Versions.Count > 4000) Versions.Clear();
                Versions[fi.FullName] = (fi.Length, fi.LastWriteTimeUtc.Ticks, v);
            }
            return v;
        }
        catch { return null; }
    }

    private sealed class Types : ICustomAttributeTypeProvider<string>
    {
        public static readonly Types I = new();
        public string GetPrimitiveType(PrimitiveTypeCode t) => t.ToString();
        public string GetSystemType() => "System.Type";
        public string GetSZArrayType(string e) => e + "[]";
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => r.GetString(r.GetTypeDefinition(h).Name);
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => r.GetString(r.GetTypeReference(h).Name);
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(string type) => type == "System.Type" || type == "Type";
    }

    private static bool IsTypeArg(string t) => t == "System.Type" || t == "Type";

    internal static string AttrName(MetadataReader md, CustomAttribute ca)
    {
        if (ca.Constructor.Kind == HandleKind.MemberReference)
            return TypeName(md, md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent);
        if (ca.Constructor.Kind == HandleKind.MethodDefinition)
            return TypeName(md, md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).GetDeclaringType());
        return "";
    }

    private static string TypeName(MetadataReader md, EntityHandle h)
    {
        if (h.Kind == HandleKind.TypeReference) return md.GetString(md.GetTypeReference((TypeReferenceHandle)h).Name);
        if (h.Kind == HandleKind.TypeDefinition) return md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)h).Name);
        return "";
    }

    private static string FullName(MetadataReader md, TypeDefinition td)
    {
        string n = md.GetString(td.Name);
        var decl = td.GetDeclaringType();
        if (!decl.IsNil) return FullName(md, md.GetTypeDefinition(decl)) + "+" + n;
        string ns = md.GetString(td.Namespace);
        return ns.Length > 0 ? ns + "." + n : n;
    }

    private static string Str(CustomAttributeTypedArgument<string> a) => a.Value as string ?? "";
    private static int Int(CustomAttributeTypedArgument<string> a) => a.Value is int i ? i : a.Value is IConvertible c ? Convert.ToInt32(c, CultureInfo.InvariantCulture) : 0;
    private static IEnumerable<CustomAttributeTypedArgument<string>> Items(CustomAttributeTypedArgument<string> a)
        => a.Value is ImmutableArray<CustomAttributeTypedArgument<string>> arr ? arr : Enumerable.Empty<CustomAttributeTypedArgument<string>>();

    private static readonly HashSet<string> AsmAttrs = new(StringComparer.Ordinal)
    {
        "MelonInfoAttribute", "MelonModInfoAttribute", "MelonPluginInfoAttribute", "MelonGameAttribute", "MelonModGameAttribute", "MelonPluginGameAttribute",
        "MelonGameVersionAttribute", "MelonPlatformAttribute", "MelonPlatformDomainAttribute", "MelonProcessAttribute",
        "MelonAdditionalDependenciesAttribute", "MelonOptionalDependenciesAttribute", "MelonIncompatibleAssembliesAttribute",
        "MelonPriorityAttribute", "VerifyLoaderVersionAttribute", "TargetFrameworkAttribute", "HarmonyDontPatchAllAttribute",
    };

    private static void ReadFrom(Stream s, ModManifest m)
    {
        using var pe = new PEReader(s, PEStreamOptions.LeaveOpen);
        if (!pe.HasMetadata) { m.Error = "нет метаданных .NET"; return; }
        var md = pe.GetMetadataReader();
        if (!md.IsAssembly) { m.Error = "это не сборка .NET"; return; }
        var asm = md.GetAssemblyDefinition();
        m.AssemblyName = md.GetString(asm.Name);
        m.AssemblyVersion = asm.Version;
        m.Readable = true;
        foreach (var rh in md.AssemblyReferences)
        {
            var r = md.GetAssemblyReference(rh);
            m.References[md.GetString(r.Name)] = r.Version;
        }
        if (m.References.TryGetValue("MelonLoader", out var ml)) m.LoaderRef = ml;
        m.UsesHarmony = m.References.ContainsKey("0Harmony");
        m.UsesUnhollower = m.References.Keys.Any(k => k.StartsWith("Unhollower", StringComparison.OrdinalIgnoreCase));
        m.UsesIl2CppInterop = m.References.Keys.Any(k => k.StartsWith("Il2CppInterop.", StringComparison.OrdinalIgnoreCase));

        foreach (var h in asm.GetCustomAttributes())
        {
            try
            {
                var ca = md.GetCustomAttribute(h);
                string an = AttrName(md, ca);
                if (AsmAttrs.Contains(an)) AssemblyAttribute(md, an, ca.DecodeValue(Types.I), m);
            }
            catch { /* одно кривое поле не должно ломать чтение */ }
        }

        foreach (var th in md.TypeReferences)
        {
            var tr = md.GetTypeReference(th);
            if (md.StringComparer.Equals(tr.Namespace, "MelonLoader") && md.GetString(tr.Name).StartsWith("MelonPreferences", StringComparison.Ordinal))
                m.UsesPreferences = true;
        }
        if (m.UsesHarmony)
        {
            foreach (var mh in md.MemberReferences)
            {
                var mr = md.GetMemberReference(mh);
                if (mr.Parent.Kind != HandleKind.TypeReference) continue;
                if (!md.StringComparer.Equals(md.GetTypeReference((TypeReferenceHandle)mr.Parent).Name, "Harmony")) continue;
                if (md.StringComparer.Equals(mr.Name, "Patch")) m.ManualPatching = true;
            }
            ReadHooks(md, m);
            if (m.ManualPatching)
                try { ReadManualHooks(pe, md, m); } catch { /* разбор IL — best effort */ }
        }
    }

    private static void AssemblyAttribute(MetadataReader md, string an, CustomAttributeValue<string> v, ModManifest m)
    {
        var a = v.FixedArguments;
        switch (an)
        {
            case "MelonInfoAttribute":
            case "MelonModInfoAttribute":
            case "MelonPluginInfoAttribute":
                m.IsMelon = true;
                m.LegacyInfo = an != "MelonInfoAttribute";
                if (a.Length >= 4) { m.MelonName = Str(a[1]); m.MelonVersion = Str(a[2]); m.MelonAuthor = Str(a[3]); }
                m.MelonKind = an == "MelonPluginInfoAttribute" ? "Plugin" : an == "MelonModInfoAttribute" ? "Mod" : GuessKind(md, a.Length > 0 ? Str(a[0]) : "");
                break;
            case "MelonGameAttribute":
            case "MelonModGameAttribute":
            case "MelonPluginGameAttribute":
                m.Games.Add((a.Length > 0 ? Str(a[0]) : "", a.Length > 1 ? Str(a[1]) : ""));
                break;
            case "MelonGameVersionAttribute":
                if (a.Length > 0 && Str(a[0]).Length > 0) m.GameVersions.Add(Str(a[0]));
                break;
            case "MelonPlatformAttribute":
                foreach (var x in a) foreach (var p in Items(x)) m.Platforms.Add(Int(p));
                break;
            case "MelonPlatformDomainAttribute":
                m.Domain = a.Length > 0 ? Int(a[0]) : 0;
                break;
            case "MelonProcessAttribute":
                if (a.Length > 0 && Str(a[0]).Length > 0) m.Processes.Add(Str(a[0]));
                break;
            case "MelonAdditionalDependenciesAttribute":
                foreach (var x in a) foreach (var p in Items(x)) if (p.Value is string s1 && s1.Length > 0) m.Additional.Add(s1);
                break;
            case "MelonOptionalDependenciesAttribute":
                foreach (var x in a) foreach (var p in Items(x)) if (p.Value is string s2 && s2.Length > 0) m.Optional.Add(s2);
                break;
            case "MelonIncompatibleAssembliesAttribute":
                foreach (var x in a) foreach (var p in Items(x)) if (p.Value is string s3 && s3.Length > 0) m.Incompatible.Add(s3);
                break;
            case "MelonPriorityAttribute":
                m.Priority = a.Length > 0 ? Int(a[0]) : 0;
                break;
            case "VerifyLoaderVersionAttribute":
            {
                var ints = a.Where(x => x.Value is int).Select(x => (int)x.Value!).ToList();
                string? sv = a.Select(x => x.Value as string).FirstOrDefault(x => !string.IsNullOrEmpty(x));
                m.RequiredLoader = sv ?? (ints.Count >= 3 ? string.Join(".", ints) : "");
                m.RequiredIsMinimum = a.Any(x => x.Value is bool b && b);
                foreach (var na in v.NamedArguments)
                    if (na.Name == "IsMinimum" && na.Value is bool nb) m.RequiredIsMinimum = nb;
                break;
            }
            case "TargetFrameworkAttribute":
                if (a.Length > 0) m.TargetFramework = Str(a[0]);
                break;
            case "HarmonyDontPatchAllAttribute":
                m.DontPatchAll = true;
                break;
        }
    }

    private static string GuessKind(MetadataReader md, string typeName)
    {
        string shortName = typeName.Split(',')[0].Trim();
        foreach (var th in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(th);
            if (FullName(md, td).Replace('+', '.') != shortName.Replace('+', '.')) continue;
            if (td.BaseType.Kind == HandleKind.TypeReference)
            {
                string b = md.GetString(md.GetTypeReference((TypeReferenceHandle)td.BaseType).Name);
                if (b == "MelonPlugin") return "Plugin";
            }
            return "Mod";
        }
        return "Mod";
    }

    // ───── Harmony: [HarmonyPatch] на классе + Prefix/Postfix/Transpiler/Finalizer ─────

    private sealed class PatchSpec
    {
        public string? Type, Method;
        public int MethodType = -1;
        public static PatchSpec Merge(IEnumerable<PatchSpec> specs)
        {   // как HarmonyMethod.Merge: более поздние непустые значения перекрывают ранние
            var r = new PatchSpec();
            foreach (var s in specs)
            {
                if (s.Type != null) r.Type = s.Type;
                if (s.Method != null) r.Method = s.Method;
                if (s.MethodType >= 0) r.MethodType = s.MethodType;
            }
            return r;
        }
    }

    private static PatchSpec DecodePatch(CustomAttributeValue<string> v)
    {
        var p = new PatchSpec();
        var strings = new List<string>();
        foreach (var a in v.FixedArguments)
        {
            if (IsTypeArg(a.Type) && a.Value is string t) p.Type = t;
            else if (a.Type == "String" && a.Value is string s) strings.Add(s);
            else if (a.Type == "MethodType") p.MethodType = Int(a);
        }
        if (strings.Count >= 2 && p.Type == null) { p.Type = strings[0]; p.Method = strings[1]; } // (string typeName, string methodName)
        else if (strings.Count >= 1) p.Method = strings[0];
        foreach (var na in v.NamedArguments)
        {
            if (na.Name is "methodName" && na.Value is string mn) p.Method = mn;
            else if (na.Name is "declaringType" && na.Value is string dt) p.Type = dt;
            else if (na.Name is "methodType") p.MethodType = na.Value is int mi ? mi : p.MethodType;
        }
        if (p.Type != null) { int c = p.Type.IndexOf(','); if (c >= 0) p.Type = p.Type.Substring(0, c); p.Type = p.Type.Trim(); }
        return p;
    }

    private static string MethodName(PatchSpec s) => s.MethodType switch
    {
        1 => "get_" + (s.Method ?? "?"),
        2 => "set_" + (s.Method ?? "?"),
        3 => ".ctor",
        4 => ".cctor",
        _ => s.Method ?? "?",
    };

    private static bool ReturnsBool(MetadataReader md, MethodDefinition def)
    {
        try
        {
            var br = md.GetBlobReader(def.Signature);
            var hdr = br.ReadSignatureHeader();
            if (hdr.IsGeneric) br.ReadCompressedInteger();
            br.ReadCompressedInteger();
            return br.ReadSignatureTypeCode() == SignatureTypeCode.Boolean;
        }
        catch { return false; }
    }

    private static void ReadHooks(MetadataReader md, ModManifest m)
    {
        foreach (var th in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(th);
            List<PatchSpec>? cls = null;
            foreach (var ah in td.GetCustomAttributes())
            {
                try
                {
                    var ca = md.GetCustomAttribute(ah);
                    string an = AttrName(md, ca);
                    if (an is "HarmonyPatch" or "HarmonyPatchAttribute") (cls ??= new()).Add(DecodePatch(ca.DecodeValue(Types.I)));
                }
                catch { (cls ??= new()).Add(new PatchSpec()); }
            }
            if (cls == null) continue; // PatchAll берёт только классы с [HarmonyPatch]
            var classSpec = PatchSpec.Merge(cls);
            bool dynamicTarget = false;
            var found = new List<(string Kind, bool Skips, PatchSpec Spec)>();
            foreach (var mh in td.GetMethods())
            {
                var def = md.GetMethodDefinition(mh);
                string name = md.GetString(def.Name);
                if (name is "TargetMethod" or "TargetMethods") { dynamicTarget = true; continue; }
                string? kind = name is "Prefix" or "Postfix" or "Transpiler" or "Finalizer" ? name : null;
                var own = new List<PatchSpec>();
                foreach (var ah in def.GetCustomAttributes())
                {
                    try
                    {
                        var ca = md.GetCustomAttribute(ah);
                        switch (AttrName(md, ca))
                        {
                            case "HarmonyPrefix": kind = "Prefix"; break;
                            case "HarmonyPostfix": kind = "Postfix"; break;
                            case "HarmonyTranspiler": kind = "Transpiler"; break;
                            case "HarmonyFinalizer": kind = "Finalizer"; break;
                            case "HarmonyPatch": own.Add(DecodePatch(ca.DecodeValue(Types.I))); break;
                        }
                    }
                    catch { }
                }
                if (kind == null || (def.Attributes & MethodAttributes.Static) == 0) continue;
                var spec = PatchSpec.Merge(new[] { classSpec }.Concat(own));
                found.Add((kind, kind == "Prefix" && ReturnsBool(md, def), spec));
            }
            string patchClass = FullName(md, td);
            foreach (var f in found)
            {
                var h = new HookTarget { Kind = f.Kind, SkipsOriginal = f.Skips, PatchClass = patchClass, Type = f.Spec.Type ?? "" };
                h.Method = dynamicTarget || f.Spec.Type == null ? "?" : MethodName(f.Spec);
                m.Hooks.Add(h);
            }
        }
    }

    // ───── Harmony: патчи в коде — harmony.Patch(AccessTools.Method(typeof(X), "Y"), new HarmonyMethod(…), …) ─────
    // Упрощённое исполнение IL внутри метода: стек значений (тип из ldtoken, строка, ссылка на метод, HarmonyMethod, null,
    // параметр метода) + один уровень вспомогательных методов вида Patch(typeof(X), "Y", …). Ветвления сбрасывают стек.

    private sealed class Val
    {
        public static readonly Val Unknown = new(), Null = new() { IsNull = true };
        public bool IsNull, IsMethod, IsProperty, IsHM;
        public string? Type;                 // ldtoken
        public bool External;                // тип из другой сборки (игра, Unity)
        public TypeDefinitionHandle Own;     // тип самого мода
        public string? Str;                  // ldstr
        public int Arg = -1;                 // ldarg
        public Val? Owner, Name;             // ссылка на метод/свойство: тип и имя
        public string Prefix = "";           // get_ / set_
        public Val? Target;                  // HarmonyMethod → метод мода
        public bool Interesting => Type != null || IsMethod || IsHM || IsProperty || Str != null;
    }

    /// Таблицы опкодов: строятся один раз и публикуются целиком (Lazy), дальше только чтение из любых потоков.
    /// Pop/Push — сколько значений снимает и кладёт инструкция, заранее для каждого StackBehaviour:
    /// раньше это считалось через ToString/Split на каждой инструкции (≈20 МБ мусора на CreatorToolkit).
    private sealed class OpTables
    {
        public readonly Dictionary<int, System.Reflection.Emit.OpCode> Ops1 = new(), Ops2 = new();
        public readonly int[] Pop, Push;
        public OpTables()
        {
            foreach (var f in typeof(System.Reflection.Emit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.GetValue(null) is System.Reflection.Emit.OpCode o)
                {
                    int v = (ushort)o.Value;
                    if (o.Size == 1) Ops1[v] = o; else Ops2[v & 0xFF] = o;
                }
            var all = (System.Reflection.Emit.StackBehaviour[])Enum.GetValues(typeof(System.Reflection.Emit.StackBehaviour));
            int max = 0;
            foreach (var b in all) max = Math.Max(max, (int)b);
            Pop = new int[max + 1]; Push = new int[max + 1];
            foreach (var b in all) { Pop[(int)b] = PopCount(b); Push[(int)b] = PushCount(b); }
        }
    }
    private static readonly Lazy<OpTables> OpsT = new(() => new OpTables());

    /// Буферы и кэши разбора одной сборки. Токены повторяются (тысячи вызовов одних и тех же методов, ldarg.0),
    /// поэтому CallInfo, строки и значения создаются один раз на токен, а стек и переменные — один раз на сборку.
    /// Val неизменяемы после создания — один экземпляр можно класть в стек сколько угодно раз.
    private sealed class IlScratch
    {
        public readonly OpTables T = OpsT.Value;
        public readonly HashSet<int> Targets = new();
        public readonly List<Val> Stack = new();
        public readonly Dictionary<int, Val> Locals = new();
        public readonly Dictionary<int, CallInfo?> Calls = new();
        public readonly Dictionary<int, Val> TokVals = new();   // ldstr / ldtoken
        public readonly List<Val> ArgVals = new();              // ldarg N
        public Val Arg(int i)
        {
            if (i < 0) return new Val { Arg = i };
            while (ArgVals.Count <= i) ArgVals.Add(new Val { Arg = ArgVals.Count });
            return ArgVals[i];
        }
    }

    private static int PopCount(System.Reflection.Emit.StackBehaviour b)
    {
        string n = b.ToString();
        if (n == "Pop0" || !n.StartsWith("Pop", StringComparison.Ordinal)) return 0;
        return n.Split('_').Length;
    }

    private static int PushCount(System.Reflection.Emit.StackBehaviour b)
    {
        string n = b.ToString();
        return n == "Push0" || !n.StartsWith("Push", StringComparison.Ordinal) ? 0 : n == "Push1_push1" ? 2 : 1;
    }

    private static (int Params, bool HasThis, bool Void) Sig(MetadataReader md, BlobHandle h)
    {
        var br = md.GetBlobReader(h);
        var hdr = br.ReadSignatureHeader();
        if (hdr.IsGeneric) br.ReadCompressedInteger();
        int n = br.ReadCompressedInteger();
        var code = br.ReadSignatureTypeCode();
        while (code is SignatureTypeCode.RequiredModifier or SignatureTypeCode.OptionalModifier) { br.ReadTypeHandle(); code = br.ReadSignatureTypeCode(); }
        return (n, hdr.IsInstance, code == SignatureTypeCode.Void);
    }

    private sealed class CallInfo
    {
        public string Ns = "", Type = "", Name = "";
        public int Params;
        public bool HasThis, Void;
        public MethodDefinitionHandle OwnMethod;
        /// harmony.Patch или вызов, который понимает Call() (typeof, GetMethod, AccessTools, new HarmonyMethod);
        /// для остальных результат всегда неизвестен — аргументы не нужны, кроме вызовов своих методов.
        public bool Special;
        public CallInfo Init()
        {
            Special = Ns == "HarmonyLib" && (Type == "Harmony" && Name == "Patch" || Type == "AccessTools" || Type == "HarmonyMethod" && Name == ".ctor")
                || Ns == "System" && Type == "Type" || Ns == "System.Reflection" && Type == "PropertyInfo";
            return this;
        }
    }

    private static CallInfo? Resolve(MetadataReader md, EntityHandle h)
    {
        switch (h.Kind)
        {
            case HandleKind.MethodDefinition:
            {
                var mh = (MethodDefinitionHandle)h;
                var def = md.GetMethodDefinition(mh);
                var td = md.GetTypeDefinition(def.GetDeclaringType());
                var (p, inst, v) = Sig(md, def.Signature);
                return new CallInfo { Ns = md.GetString(td.Namespace), Type = md.GetString(td.Name), Name = md.GetString(def.Name), Params = p, HasThis = inst, Void = v, OwnMethod = mh };
            }
            case HandleKind.MemberReference:
            {
                var mr = md.GetMemberReference((MemberReferenceHandle)h);
                if (mr.GetKind() != MemberReferenceKind.Method) return null;
                var (p, inst, v) = Sig(md, mr.Signature);
                var c = new CallInfo { Name = md.GetString(mr.Name), Params = p, HasThis = inst, Void = v };
                if (mr.Parent.Kind == HandleKind.TypeReference)
                {
                    var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
                    c.Ns = md.GetString(tr.Namespace); c.Type = md.GetString(tr.Name);
                }
                return c;
            }
            case HandleKind.MethodSpecification:
                return Resolve(md, md.GetMethodSpecification((MethodSpecificationHandle)h).Method);
        }
        return null;
    }

    private static string TypeRefName(MetadataReader md, TypeReferenceHandle h)
    {
        var tr = md.GetTypeReference(h);
        string n = md.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference) return TypeRefName(md, (TypeReferenceHandle)tr.ResolutionScope) + "+" + n;
        string ns = md.GetString(tr.Namespace);
        return ns.Length > 0 ? ns + "." + n : n;
    }

    private static Val MethodRef(Val owner, Val name, string prefix = "") => new() { IsMethod = true, Owner = owner, Name = name, Prefix = prefix };

    private static Val Subst(Val v, Val[]? args) => v.Arg >= 0 && args != null ? (v.Arg < args.Length ? args[v.Arg] : Val.Unknown) : v;

    private static Val Call(CallInfo c, Val[] a)
    {
        Val A(int i) => i < a.Length ? a[i] : Val.Unknown;
        switch (c.Type)
        {
            case "Type" when c.Ns == "System":
                if (c.Name == "GetTypeFromHandle") return A(0);
                if (c.Name == "GetMethod") return MethodRef(A(0), A(1));
                if (c.Name == "GetProperty") return new Val { IsProperty = true, Owner = A(0), Name = A(1) };
                if (c.Name == "GetConstructor") return MethodRef(A(0), new Val { Str = ".ctor" });
                break;
            case "PropertyInfo" when c.Ns == "System.Reflection":
                if (A(0).IsProperty && c.Name is "GetGetMethod" or "get_GetMethod") return MethodRef(A(0).Owner!, A(0).Name!, "get_");
                if (A(0).IsProperty && c.Name is "GetSetMethod" or "get_SetMethod") return MethodRef(A(0).Owner!, A(0).Name!, "set_");
                break;
            case "AccessTools" when c.Ns == "HarmonyLib":
                switch (c.Name)
                {
                    case "Method": case "DeclaredMethod":
                        if (A(0).Str is string tm && tm.Contains(':'))
                        {
                            int i = tm.IndexOf(':');
                            return MethodRef(new Val { Type = tm.Substring(0, i), External = true }, new Val { Str = tm.Substring(i + 1) });
                        }
                        return MethodRef(A(0), A(1));
                    case "PropertyGetter": case "DeclaredPropertyGetter": return MethodRef(A(0), A(1), "get_");
                    case "PropertySetter": case "DeclaredPropertySetter": return MethodRef(A(0), A(1), "set_");
                    case "Constructor": case "DeclaredConstructor": return MethodRef(A(0), new Val { Str = ".ctor" });
                }
                break;
            case "HarmonyMethod" when c.Ns == "HarmonyLib" && c.Name == ".ctor":
                if (A(0).IsMethod) return new Val { IsHM = true, Target = A(0) };
                if (A(0).Type != null && A(1).Str != null) return new Val { IsHM = true, Target = MethodRef(A(0), A(1)) };
                return new Val { IsHM = true, Target = A(0).Arg >= 0 ? A(0) : Val.Unknown };
        }
        return Val.Unknown;
    }

    private sealed class PatchCall
    {
        public Val Original = Val.Unknown;
        public Val[] Patches = Array.Empty<Val>();   // prefix, postfix, transpiler, finalizer
        public string Class = "";
    }

    private static Val Pop(List<Val> st)
    {
        if (st.Count == 0) return Val.Unknown;
        var v = st[^1]; st.RemoveAt(st.Count - 1); return v;
    }

    private static void Interpret(MetadataReader md, MethodBodyBlock body, string cls, List<PatchCall> patches, List<(MethodDefinitionHandle Callee, Val[] Args, string Class)> ownCalls, IlScratch sc)
    {
        var T = sc.T;
        var il = body.GetILReader();
        var targets = sc.Targets;
        targets.Clear();
        // проход 1: цели переходов (там стек неизвестен — сбрасываем)
        while (il.RemainingBytes > 0)
        {
            int b = il.ReadByte();
            System.Reflection.Emit.OpCode op;
            if (b == 0xFE) { if (il.RemainingBytes == 0 || !T.Ops2.TryGetValue(il.ReadByte(), out op)) return; }
            else if (!T.Ops1.TryGetValue(b, out op)) return;
            switch (op.OperandType)
            {
                case System.Reflection.Emit.OperandType.ShortInlineBrTarget: { int d = il.ReadSByte(); targets.Add(il.Offset + d); break; }
                case System.Reflection.Emit.OperandType.InlineBrTarget: { int d = il.ReadInt32(); targets.Add(il.Offset + d); break; }
                case System.Reflection.Emit.OperandType.InlineSwitch:
                {   // смещения считаются от конца таблицы
                    int n = il.ReadInt32();
                    if (n < 0 || n > il.RemainingBytes / 4) throw new BadImageFormatException();
                    int end = il.Offset + 4 * n;
                    for (int i = 0; i < n; i++) targets.Add(end + il.ReadInt32());
                    break;
                }
                default: Skip(ref il, op.OperandType); break;
            }
        }
        foreach (var r in body.ExceptionRegions) { targets.Add(r.HandlerOffset); if (r.FilterOffset >= 0) targets.Add(r.FilterOffset); }

        il.Reset();
        var st = sc.Stack;
        var locals = sc.Locals;
        st.Clear(); locals.Clear();
        while (il.RemainingBytes > 0)
        {
            if (targets.Contains(il.Offset)) st.Clear();
            int b = il.ReadByte();
            System.Reflection.Emit.OpCode op;
            if (b == 0xFE) { if (il.RemainingBytes == 0 || !T.Ops2.TryGetValue(il.ReadByte(), out op)) return; }
            else if (!T.Ops1.TryGetValue(b, out op)) return;
            int v = (ushort)op.Value;
            int tok = 0, idx = -1;
            switch (op.OperandType)
            {
                case System.Reflection.Emit.OperandType.InlineMethod:
                case System.Reflection.Emit.OperandType.InlineTok:
                case System.Reflection.Emit.OperandType.InlineString:
                case System.Reflection.Emit.OperandType.InlineType:
                case System.Reflection.Emit.OperandType.InlineField:
                case System.Reflection.Emit.OperandType.InlineSig:
                    tok = il.ReadInt32(); break;
                case System.Reflection.Emit.OperandType.ShortInlineVar: idx = il.ReadByte(); break;
                case System.Reflection.Emit.OperandType.InlineVar: idx = il.ReadUInt16(); break;
                default: Skip(ref il, op.OperandType); break;
            }
            switch (v)
            {
                case 0x14: st.Add(Val.Null); continue;                                       // ldnull
                case 0x72:                                                                    // ldstr
                    if (!sc.TokVals.TryGetValue(tok, out var sv))
                        sc.TokVals[tok] = sv = new Val { Str = md.GetUserString(MetadataTokens.UserStringHandle(tok & 0xFFFFFF)) };
                    st.Add(sv); continue;
                case 0xD0:                                                                    // ldtoken
                {
                    if (sc.TokVals.TryGetValue(tok, out var tv)) { st.Add(tv); continue; }
                    var h = MetadataTokens.EntityHandle(tok);
                    if (h.Kind == HandleKind.TypeReference) tv = new Val { Type = TypeRefName(md, (TypeReferenceHandle)h), External = true };
                    else if (h.Kind == HandleKind.TypeDefinition) tv = new Val { Type = FullName(md, md.GetTypeDefinition((TypeDefinitionHandle)h)), Own = (TypeDefinitionHandle)h };
                    else tv = Val.Unknown;
                    sc.TokVals[tok] = tv;
                    st.Add(tv);
                    continue;
                }
                case >= 0x02 and <= 0x05: st.Add(sc.Arg(v - 0x02)); continue;              // ldarg.0–3
                case 0x0E: case 0xFE09: st.Add(sc.Arg(idx)); continue;                      // ldarg.s / ldarg
                case >= 0x06 and <= 0x09: st.Add(locals.TryGetValue(v - 0x06, out var l0) ? l0 : Val.Unknown); continue; // ldloc.0–3
                case 0x11: case 0xFE0C: st.Add(locals.TryGetValue(idx, out var l1) ? l1 : Val.Unknown); continue;          // ldloc.s / ldloc
                case >= 0x0A and <= 0x0D: locals[v - 0x0A] = Pop(st); continue;              // stloc.0–3
                case 0x13: case 0xFE0E: locals[idx] = Pop(st); continue;                     // stloc.s / stloc
                case 0x25: { var t = Pop(st); st.Add(t); st.Add(t); continue; }             // dup
                case 0x28: case 0x6F: case 0x73:                                              // call / callvirt / newobj
                {
                    if (!sc.Calls.TryGetValue(tok, out var c))
                    {
                        try { c = Resolve(md, MetadataTokens.EntityHandle(tok))?.Init(); } catch { c = null; }
                        sc.Calls[tok] = c;
                    }
                    if (c == null) { st.Clear(); continue; }
                    bool ctor = v == 0x73;
                    int n = c.Params + (c.HasThis && !ctor ? 1 : 0);
                    if (!c.Special)
                    {   // результат неизвестен; аргументы нужны, только если это свой метод и среди них есть типы/строки/методы
                        int k = Math.Min(n, st.Count), from = st.Count - k;
                        if (!c.OwnMethod.IsNil)
                        {
                            bool any = false;
                            for (int i = from; i < st.Count && !any; i++) any = st[i].Interesting;
                            if (any)
                            {   // как Pop() по одному: недостающие снизу — неизвестны
                                var oa = new Val[n];
                                for (int i = 0; i < n - k; i++) oa[i] = Val.Unknown;
                                for (int i = 0; i < k; i++) oa[n - k + i] = st[from + i];
                                ownCalls.Add((c.OwnMethod, oa, cls));
                            }
                        }
                        st.RemoveRange(from, k);
                        if (ctor || !c.Void) st.Add(Val.Unknown);
                        continue;
                    }
                    var a = n == 0 ? Array.Empty<Val>() : new Val[n];
                    for (int i = n - 1; i >= 0; i--) a[i] = Pop(st);
                    if (c.Name == "Patch" && c.Type == "Harmony" && c.Ns == "HarmonyLib" && c.HasThis && a.Length >= 2)
                        patches.Add(new PatchCall { Original = a[1], Patches = a.Skip(2).Take(4).ToArray(), Class = cls });
                    else if (!c.OwnMethod.IsNil && a.Any(x => x.Interesting))
                        ownCalls.Add((c.OwnMethod, a, cls));
                    if (ctor) { var cv = Call(c, a); st.Add(cv.IsHM ? cv : Val.Unknown); }
                    else if (!c.Void) st.Add(Call(c, a));
                    continue;
                }
            }
            if (op.FlowControl is System.Reflection.Emit.FlowControl.Branch or System.Reflection.Emit.FlowControl.Return or System.Reflection.Emit.FlowControl.Throw)
            { st.Clear(); continue; }
            if (op.StackBehaviourPop == System.Reflection.Emit.StackBehaviour.Varpop || op.StackBehaviourPush == System.Reflection.Emit.StackBehaviour.Varpush) { st.Clear(); continue; }
            for (int i = T.Pop[(int)op.StackBehaviourPop]; i > 0; i--) Pop(st);
            for (int i = T.Push[(int)op.StackBehaviourPush]; i > 0; i--) st.Add(Val.Unknown);
        }
    }

    private static void Skip(ref BlobReader il, System.Reflection.Emit.OperandType t)
    {
        switch (t)
        {
            case System.Reflection.Emit.OperandType.InlineNone: break;
            case System.Reflection.Emit.OperandType.ShortInlineBrTarget:
            case System.Reflection.Emit.OperandType.ShortInlineI:
            case System.Reflection.Emit.OperandType.ShortInlineVar: il.Offset += 1; break;
            case System.Reflection.Emit.OperandType.InlineVar: il.Offset += 2; break;
            case System.Reflection.Emit.OperandType.InlineI8:
            case System.Reflection.Emit.OperandType.InlineR: il.Offset += 8; break;
            case System.Reflection.Emit.OperandType.InlineSwitch: { int n = il.ReadInt32(); il.Offset += 4 * n; break; }
            default: il.Offset += 4; break;
        }
    }

    private static void ReadManualHooks(PEReader pe, MetadataReader md, ModManifest m)
    {
        var sc = new IlScratch();
        var patches = new List<PatchCall>();
        var ownCalls = new List<(MethodDefinitionHandle Callee, Val[] Args, string Class)>();
        var helperOf = new Dictionary<MethodDefinitionHandle, List<PatchCall>>();
        foreach (var th in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(th);
            string cls = FullName(md, td);
            foreach (var mh in td.GetMethods())
            {
                var def = md.GetMethodDefinition(mh);
                if (def.RelativeVirtualAddress == 0) continue;
                int before = patches.Count;
                try { Interpret(md, pe.GetMethodBody(def.RelativeVirtualAddress), cls, patches, ownCalls, sc); } catch { continue; }
                for (int i = before; i < patches.Count; i++)
                {   // Patch(original = параметр или AccessTools.Method(параметр, параметр)) — вспомогательный метод
                    var o = patches[i].Original;
                    bool viaArgs = o.Arg >= 0 || o.IsMethod && (o.Owner?.Arg >= 0 || o.Name?.Arg >= 0);
                    if (viaArgs) (helperOf.TryGetValue(mh, out var hl) ? hl : helperOf[mh] = new()).Add(patches[i]);
                }
            }
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Emit(PatchCall pc, Val[]? args, string cls)
        {
            var o = Subst(pc.Original, args);
            if (!o.IsMethod) return;
            var owner = Subst(o.Owner ?? Val.Unknown, args);
            var name = Subst(o.Name ?? Val.Unknown, args);
            if (owner.Type == null || !owner.External || name.Str == null) return;
            string method = name.Str == ".ctor" ? ".ctor" : o.Prefix + name.Str;
            string[] kinds = { "Prefix", "Postfix", "Transpiler", "Finalizer" };
            var found = new List<(string Kind, bool Skips)>();
            bool unknown = false;
            for (int i = 0; i < pc.Patches.Length && i < 4; i++)
            {
                var p = Subst(pc.Patches[i], args);
                if (p.IsNull) continue;
                if (!p.IsHM) { unknown = true; continue; }
                bool skips = false;
                var t = p.Target != null ? Subst(p.Target, args) : null;
                if (i == 0 && t is { IsMethod: true })
                {
                    var tOwner = Subst(t.Owner ?? Val.Unknown, args);
                    var tName = Subst(t.Name ?? Val.Unknown, args);
                    if (!tOwner.Own.IsNil && tName.Str != null)
                        foreach (var pm in md.GetTypeDefinition(tOwner.Own).GetMethods())
                        {
                            var pd = md.GetMethodDefinition(pm);
                            if (md.StringComparer.Equals(pd.Name, tName.Str) && ReturnsBool(md, pd)) { skips = true; break; }
                        }
                }
                found.Add((kinds[i], skips));
            }
            if (found.Count == 0 && unknown) found.Add(("Patch", false));
            foreach (var (kind, skips) in found)
                if (seen.Add(owner.Type + "::" + method + "|" + kind))
                    m.Hooks.Add(new HookTarget { Type = owner.Type, Method = method, Kind = kind, SkipsOriginal = skips, PatchClass = cls, Manual = true });
        }
        foreach (var pc in patches) Emit(pc, null, pc.Class);
        foreach (var (callee, args, cls) in ownCalls)
            if (helperOf.TryGetValue(callee, out var list))
                foreach (var pc in list) Emit(pc, args, cls);
    }
}

/// Окружение: MelonLoader, Unity, .NET, сгенерированные сборки игры, библиотеки, MelonPreferences.cfg.
public sealed class LoaderEnv
{
    public string GameDir = "";
    public bool LoaderInstalled, Bootstrap;
    public Version? Loader;                  // 0.7.3
    public string LoaderText = "";           // «0.7.3 Open-Beta»
    public bool OpenBeta;
    public string Runtime = "", GameType = "", GameArch = "";
    public string UnityVersion = "", GameVersion = "", GameDeveloper = "", GameName = "";
    public Version? Il2CppInterop, Harmony;
    public bool Tomlet, SupportModule;
    public int GameAssemblies = -1;          // DLL в MelonLoader\Il2CppAssemblies (-1 — папки нет)
    public string GeneratorUnity = "";       // для какой Unity сгенерированы сборки
    public bool GeneratorStale;              // сборки игры сгенерированы для другой версии игры/Unity
    public string GeneratorNote = "";
    public bool HashChecked;
    public string PrefsError = "";           // ошибка в MelonPreferences.cfg ("" — всё хорошо)
    public Version? DotNet;                  // .NET, на котором MelonLoader запускает моды
    public string DotNetDir = "";
    public List<string> DotNetInstalled = new();
    public string RollForward = "";
    public readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase);      // сборка → путь
    public readonly Dictionary<string, string> KnownWhere = new(StringComparer.OrdinalIgnoreCase); // framework / MelonLoader / game / UserLibs / Plugins
    public List<CompatIssue> Issues = new();
    public DateTime Detected;
    public TimeSpan Took;

    public string LoaderShort => Loader == null ? "" : CompatChecker.V3(Loader);

    /// «MelonLoader 0.7.3 Open-Beta · Unity 6000.0.73f1 · игра 1.3.0 · .NET 6.0.16»
    public string Summary
    {
        get
        {
            var p = new List<string>();
            p.Add(LoaderInstalled ? "MelonLoader " + (LoaderText.Length > 0 ? LoaderText : LoaderShort) : "MelonLoader не установлен");
            if (UnityVersion.Length > 0) p.Add("Unity " + UnityVersion);
            if (GameVersion.Length > 0) p.Add("игра " + ShortGameVersion);
            if (DotNet != null) p.Add(".NET " + DotNet);
            return string.Join(" · ", p);
        }
    }

    public string ShortGameVersion { get { int i = GameVersion.IndexOf(' '); return i > 0 ? GameVersion.Substring(0, i) : GameVersion; } }

    public Version? VersionOf(string asm) => Known.TryGetValue(asm, out var p) ? ManifestReader.AssemblyVersionOf(p) : null;
    public string WhereOf(string asm) => KnownWhere.TryGetValue(asm, out var w) ? w : "";

    private static readonly Regex LoaderRx = new(@"MelonLoader v(\d+\.\d+\.\d+(?:\.\d+)?)(?:\s+([A-Za-z][\w-]*))?", RegexOptions.CultureInvariant);
    private static string Field(string line, string name)
    {
        int i = line.IndexOf(name + ":", StringComparison.Ordinal);
        return i < 0 ? "" : line.Substring(i + name.Length + 1).Trim();
    }

    /// Определить окружение. hashGameAssembly — сверить хэш GameAssembly.dll с тем, для которого
    /// MelonLoader генерировал сборки (результат кэшируется в UserData\ModManager\compat-cache.json).
    public static LoaderEnv Detect(ModStore store, bool hashGameAssembly = true, string? cacheDir = null)
    {
        var sw = Stopwatch.StartNew();
        var env = new LoaderEnv { GameDir = store.GameDir, Detected = DateTime.Now };
        string mlDir = IOPath.Combine(store.GameDir, "MelonLoader");
        string net6 = IOPath.Combine(mlDir, "net6");
        string mlDll = IOPath.Combine(net6, "MelonLoader.dll");
        if (!File.Exists(mlDll) && File.Exists(IOPath.Combine(mlDir, "MelonLoader.dll"))) mlDll = IOPath.Combine(mlDir, "MelonLoader.dll"); // 0.5 и старше
        env.LoaderInstalled = File.Exists(mlDll);
        env.Bootstrap = new[] { "version.dll", "winhttp.dll", "winmm.dll", "dobby.dll" }.Any(f => File.Exists(IOPath.Combine(store.GameDir, f)));
        if (env.LoaderInstalled) env.Loader = ManifestReader.AssemblyVersionOf(mlDll);

        // ── шапка журнала: версия MelonLoader, Unity, игра, .NET
        string hostFxr = "";
        try
        {
            if (File.Exists(store.MelonLogPath))
            {
                var logTime = File.GetLastWriteTime(store.MelonLogPath);
                using var fs = new FileStream(store.MelonLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var rd = new StreamReader(fs, Encoding.UTF8, true);
                string logLoader = "", logTag = "";
                for (int i = 0; i < 80; i++)
                {
                    string? line = rd.ReadLine();
                    if (line == null) break;
                    var lm = LoaderRx.Match(line);
                    if (lm.Success && logLoader.Length == 0) { logLoader = lm.Groups[1].Value; logTag = lm.Groups[2].Value; continue; }
                    string f;
                    if ((f = Field(line, "Runtime Type")).Length > 0) env.Runtime = f;
                    else if ((f = Field(line, "Game Type")).Length > 0) env.GameType = f;
                    else if ((f = Field(line, "Game Arch")).Length > 0) env.GameArch = f;
                    else if ((f = Field(line, "Game Name")).Length > 0) env.GameName = f;
                    else if ((f = Field(line, "Game Developer")).Length > 0) env.GameDeveloper = f;
                    else if ((f = Field(line, "Unity Version")).Length > 0) env.UnityVersion = f;
                    else if ((f = Field(line, "Game Version")).Length > 0) env.GameVersion = f;
                    else if ((f = Field(line, "Using HostFXR Path")).Length > 0) hostFxr = f;
                    if (line.Contains("Loading UserLibs", StringComparison.Ordinal)) break; // шапка закончилась
                }
                if (Version.TryParse(logLoader, out var lv))
                {
                    bool same = env.Loader == null || CompatChecker.V3(env.Loader) == CompatChecker.V3(lv);
                    env.Loader ??= lv;
                    if (same && logTag.Length > 0) { env.OpenBeta = logTag.Equals("Open-Beta", StringComparison.OrdinalIgnoreCase); env.LoaderText = CompatChecker.V3(lv) + " " + logTag; }
                }
            }
        }
        catch { }
        if (env.Loader != null && env.LoaderText.Length == 0) env.LoaderText = CompatChecker.V3(env.Loader);
        if (env.Runtime.Length == 0 && File.Exists(IOPath.Combine(net6, "MelonLoader.dll"))) env.Runtime = "net6";

        // ── Unity и игра (сами файлы игры — точнее старого журнала)
        try
        {
            string up = IOPath.Combine(store.GameDir, "UnityPlayer.dll");
            if (File.Exists(up))
            {
                string pv = FileVersionInfo.GetVersionInfo(up).ProductVersion ?? "";
                string uv = pv.Split(' ', '(')[0].Trim();
                if (Regex.IsMatch(uv, @"^\d+\.\d+\.\d+")) env.UnityVersion = uv;
            }
        }
        catch { }
        try
        {
            string info = IOPath.Combine(store.GameDir, IOPath.GetFileNameWithoutExtension(store.GameExe) + "_Data", "app.info");
            if (File.Exists(info))
            {
                var lines = File.ReadAllLines(info);
                if (lines.Length >= 2) { env.GameDeveloper = lines[0].Trim(); env.GameName = lines[1].Trim(); }
            }
        }
        catch { }

        // ── библиотеки MelonLoader
        env.Il2CppInterop = ManifestReader.AssemblyVersionOf(IOPath.Combine(net6, "Il2CppInterop.Runtime.dll"));
        env.Harmony = ManifestReader.AssemblyVersionOf(IOPath.Combine(net6, "0Harmony.dll"));
        env.Tomlet = File.Exists(IOPath.Combine(net6, "Tomlet.dll"));
        env.SupportModule = File.Exists(IOPath.Combine(mlDir, "Dependencies", "SupportModules", "Il2Cpp.dll"));
        Index(env, net6, "MelonLoader");
        string il2 = IOPath.Combine(mlDir, "Il2CppAssemblies");
        if (Directory.Exists(il2)) env.GameAssemblies = Index(env, il2, "game");
        Index(env, store.UserLibsDir, "UserLibs");
        Index(env, store.PluginsDir, "Plugins");

        // ── .NET: MelonLoader.runtimeconfig.json (обычно net6.0 + LatestMinor) и установленные рантаймы
        try
        {
            Version want = new(6, 0, 0);
            string roll = "Minor";
            string rc = IOPath.Combine(net6, "MelonLoader.runtimeconfig.json");
            if (File.Exists(rc))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(rc), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                var ro = doc.RootElement.GetProperty("runtimeOptions");
                if (ro.TryGetProperty("rollForward", out var rf) && rf.ValueKind == JsonValueKind.String) roll = rf.GetString() ?? roll;
                if (ro.TryGetProperty("framework", out var fw) && fw.TryGetProperty("version", out var fv) && Version.TryParse(fv.GetString(), out var wv)) want = wv;
            }
            env.RollForward = roll;
            var roots = new List<string>();
            if (hostFxr.Length > 0)
            {   // …\dotnet\host\fxr\10.0.12\hostfxr.dll → …\dotnet
                var d = IOPath.GetDirectoryName(hostFxr);
                for (int i = 0; i < 3 && d != null; i++) d = IOPath.GetDirectoryName(d);
                if (d != null) roots.Add(d);
            }
            string? dr = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(dr)) roots.Add(dr);
            roots.Add(IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
            var found = new List<(Version V, string Dir)>();
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string shared = IOPath.Combine(root, "shared", "Microsoft.NETCore.App");
                if (!Directory.Exists(shared)) continue;
                foreach (var d in Directory.GetDirectories(shared))
                {
                    string name = IOPath.GetFileName(d);
                    if (Version.TryParse(name.Split('-')[0], out var v) && !name.Contains('-')) found.Add((v, d));
                }
                if (found.Count > 0) break;
            }
            env.DotNetInstalled = found.OrderBy(f => f.V).Select(f => f.V.ToString()).ToList();
            bool anyMajor = roll.Equals("Major", StringComparison.OrdinalIgnoreCase) || roll.Equals("LatestMajor", StringComparison.OrdinalIgnoreCase);
            var pick = found.Where(f => f.V >= want && (anyMajor || f.V.Major == want.Major))
                            .OrderByDescending(f => f.V).Select(f => ((Version V, string Dir)?)f).FirstOrDefault();
            if (pick != null)
            {
                env.DotNet = pick.Value.V;
                env.DotNetDir = pick.Value.Dir;
                Index(env, env.DotNetDir, "framework");
            }
        }
        catch { }

        // ── сборки игры: для какой версии сгенерированы (Il2CppAssemblyGenerator\Config.cfg)
        try
        {
            string cfg = IOPath.Combine(mlDir, "Dependencies", "Il2CppAssemblyGenerator", "Config.cfg");
            if (File.Exists(cfg))
            {
                string text = File.ReadAllText(cfg);
                var um = Regex.Match(text, @"UnityVersion\s*=\s*""([^""]*)""");
                var hm = Regex.Match(text, @"GameAssemblyHash\s*=\s*""([^""]*)""");
                env.GeneratorUnity = um.Success ? um.Groups[1].Value : "";
                string actual = Regex.Match(env.UnityVersion, @"^\d+\.\d+\.\d+").Value;
                if (env.GeneratorUnity.Length > 0 && actual.Length > 0 && !env.GeneratorUnity.StartsWith(actual, StringComparison.OrdinalIgnoreCase) && !actual.StartsWith(env.GeneratorUnity, StringComparison.OrdinalIgnoreCase))
                {
                    env.GeneratorStale = true;
                    env.GeneratorNote = $"сборки сгенерированы для Unity {env.GeneratorUnity}, а игра на {env.UnityVersion}";
                }
                string ga = IOPath.Combine(store.GameDir, "GameAssembly.dll");
                if (!env.GeneratorStale && hashGameAssembly && hm.Success && hm.Groups[1].Value.Length > 0 && File.Exists(ga))
                {
                    string? h = GameAssemblyHash(cacheDir ?? store.ConfigDir, ga);
                    if (h != null)
                    {
                        env.HashChecked = true;
                        if (!h.Equals(hm.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                        {
                            env.GeneratorStale = true;
                            env.GeneratorNote = "GameAssembly.dll изменилась после генерации сборок (игра обновилась)";
                        }
                    }
                }
            }
        }
        catch { }

        // ── MelonPreferences.cfg
        try { if (File.Exists(store.PreferencesPath)) env.PrefsError = TomlCheck.Problem(File.ReadAllText(store.PreferencesPath)); }
        catch (Exception e) { env.PrefsError = "не читается: " + e.Message; }

        env.Issues = CompatChecker.EnvIssues(env);
        env.Took = sw.Elapsed;
        return env;
    }

    private static int Index(LoaderEnv env, string dir, string where)
    {
        int n = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            foreach (var f in Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
            {
                string name = IOPath.GetFileNameWithoutExtension(f);
                if (!env.Known.ContainsKey(name)) { env.Known[name] = f; env.KnownWhere[name] = where; }
                n++;
            }
        }
        catch { }
        return n;
    }

    private sealed class HashCache
    {
        [JsonPropertyName("gameAssembly")] public string Path { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("ticks")] public long Ticks { get; set; }
        [JsonPropertyName("sha512")] public string Sha512 { get; set; } = "";
    }

    /// SHA-512 GameAssembly.dll (как в Config.cfg генератора MelonLoader). Считается один раз на версию файла.
    private static string? GameAssemblyHash(string cacheDir, string ga)
    {
        var fi = new FileInfo(ga);
        string cache = IOPath.Combine(cacheDir, "compat-cache.json");
        try
        {
            if (File.Exists(cache))
            {
                var c = JsonSerializer.Deserialize<HashCache>(File.ReadAllText(cache));
                if (c != null && c.Size == fi.Length && c.Ticks == fi.LastWriteTimeUtc.Ticks && c.Sha512.Length > 0) return c.Sha512;
            }
        }
        catch { }
        try
        {
            string h;
            using (var fs = new FileStream(ga, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20))
            using (var sha = SHA512.Create())
                h = Convert.ToHexString(sha.ComputeHash(fs));
            try
            {
                Directory.CreateDirectory(cacheDir);
                File.WriteAllText(cache, JsonSerializer.Serialize(new HashCache { Path = ga, Size = fi.Length, Ticks = fi.LastWriteTimeUtc.Ticks, Sha512 = h }));
            }
            catch { }
            return h;
        }
        catch { return null; }
    }
}

/// Лёгкая проверка TOML (MelonPreferences.cfg): незакрытые кавычки и скобки, повторы разделов и ключей,
/// строки без «ключ = значение». Tomlet на таком файле падает, и MelonLoader пересоздаёт настройки.
public static class TomlCheck
{
    public static string Problem(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var tables = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        string table = "";
        int depth = 0;
        string? multi = null;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            int no = i + 1;
            if (multi != null) { if (line.Contains(multi, StringComparison.Ordinal)) multi = null; continue; }
            if (depth > 0)
            {
                depth += Balance(line, out bool okq);
                if (!okq) return $"строка {no}: не закрыты кавычки";
                if (depth < 0) return $"строка {no}: лишняя закрывающая скобка";
                continue;
            }
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[')
            {
                bool arr = line.StartsWith("[[", StringComparison.Ordinal);
                int close = line.IndexOf(arr ? "]]" : "]", StringComparison.Ordinal);
                if (close < 0) return $"строка {no}: не закрыт заголовок раздела";
                string name = line.Substring(arr ? 2 : 1, close - (arr ? 2 : 1)).Trim();
                string rest = line.Substring(close + (arr ? 2 : 1)).Trim();
                if (name.Length == 0) return $"строка {no}: пустое имя раздела";
                if (rest.Length > 0 && rest[0] != '#') return $"строка {no}: лишний текст после [{name}]";
                if (!arr && !tables.Add(name)) return $"строка {no}: раздел [{name}] повторяется";
                table = name;
                continue;
            }
            int eq = EqualsOutsideQuotes(line);
            if (eq <= 0) return $"строка {no}: ожидалось «ключ = значение»";
            string key = line.Substring(0, eq).Trim();
            string val = line.Substring(eq + 1).Trim();
            if (val.Length == 0) return $"строка {no}: у «{key}» нет значения";
            if (!keys.Add(table + "\u0001" + key)) return $"строка {no}: «{key}» в [{table}] повторяется";
            if (val.StartsWith("\"\"\"", StringComparison.Ordinal) || val.StartsWith("'''", StringComparison.Ordinal))
            {
                string d = val.Substring(0, 3);
                if (val.IndexOf(d, 3, StringComparison.Ordinal) < 0) multi = d;
                continue;
            }
            char c0 = val[0];
            if (!(c0 is '"' or '\'' or '[' or '{' or '+' or '-' || char.IsDigit(c0) || val.StartsWith("true") || val.StartsWith("false") || val.StartsWith("inf") || val.StartsWith("nan")))
                return $"строка {no}: непонятное значение «{(val.Length > 30 ? val.Substring(0, 30) + "…" : val)}»";
            int b = Balance(val, out bool ok);
            if (!ok) return $"строка {no}: не закрыты кавычки в «{key}»";
            if (b < 0) return $"строка {no}: лишняя закрывающая скобка в «{key}»";
            depth = b;
        }
        if (multi != null) return "файл обрывается внутри многострочной строки";
        if (depth > 0) return "файл обрывается внутри массива";
        return "";
    }

    private static int EqualsOutsideQuotes(string s)
    {
        char q = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (q != '\0') { if (c == q) q = '\0'; continue; }
            if (c is '"' or '\'') q = c;
            else if (c == '=') return i;
            else if (c == '#') return -1;
        }
        return -1;
    }

    /// Изменение глубины скобок [ ] { } вне строк; ok = false — строка не закрыта.
    private static int Balance(string s, out bool ok)
    {
        int d = 0;
        char q = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (q == '"') { if (c == '\\') i++; else if (c == '"') q = '\0'; continue; }
            if (q == '\'') { if (c == '\'') q = '\0'; continue; }
            if (c is '"' or '\'') q = c;
            else if (c == '#') break;
            else if (c is '[' or '{') d++;
            else if (c is ']' or '}') d--;
        }
        ok = q == '\0';
        return d;
    }
}

/// Итог проверки: метки по модам + окружение.
public sealed class CompatReport
{
    public LoaderEnv Env = new();
    public IReadOnlyDictionary<string, ModManifest> Manifests = new Dictionary<string, ModManifest>();
    public readonly Dictionary<string, List<CompatIssue>> ByMod = new(StringComparer.OrdinalIgnoreCase);
    public static readonly List<CompatIssue> None = new();
    public TimeSpan Took;

    public List<CompatIssue> For(string id) => id != null && ByMod.TryGetValue(id, out var l) ? l : None;
    public CompatLevel Worst(string id) { var l = For(id); return l.Count == 0 ? CompatLevel.Ok : l.Max(i => i.Level); }
    public ModManifest? Manifest(string id) => id != null && Manifests.TryGetValue(id, out var m) ? m : null;

    /// Что мешает запуску: ошибки у включённых модов и у окружения.
    public List<(ModEntry? Mod, CompatIssue Issue)> Blocking(IReadOnlyList<ModEntry> mods, Func<string, bool> isOn)
    {
        var res = new List<(ModEntry?, CompatIssue)>();
        foreach (var i in Env.Issues.Where(i => i.Level == CompatLevel.Error)) res.Add((null, i));
        foreach (var m in mods)
            if (!m.Available && isOn(m.Id))
                foreach (var i in For(m.Id).Where(i => i.Level == CompatLevel.Error)) res.Add((m, i));
        return res;
    }
}

public static class CompatChecker
{
    public static string V3(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
    private static string V2(Version v) => $"{v.Major}.{v.Minor}";
    private static bool Eq(string? a, string? b) => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string Norm(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static CompatIssue Issue(CompatLevel level, string code, string badge, string title, string text, string fix = "")
        => new() { Level = level, Code = code, Badge = badge, Title = title, Text = text, Fix = fix };

    /// Проблемы окружения (общие для всех модов).
    public static List<CompatIssue> EnvIssues(LoaderEnv env)
    {
        var l = new List<CompatIssue>();
        if (!env.LoaderInstalled)
        {
            l.Add(Issue(CompatLevel.Error, "loader-missing", "нет MelonLoader", "MelonLoader не установлен",
                "В папке игры нет MelonLoader — игра запустится без модов.",
                "Установите MelonLoader 0.7.x (Open-Beta) через MelonLoader.Installer, выбрав SlimeRancher2.exe."));
            return l;
        }
        var L = env.Loader;
        if (L != null && L < new Version(0, 6, 0))
            l.Add(Issue(CompatLevel.Error, "loader-old", "MelonLoader " + V3(L), "MelonLoader " + V3(L) + " устарел",
                "С версии 0.6 MelonLoader запускает Il2Cpp-моды на .NET 6 и Il2CppInterop (вместо Unhollower). Современные моды для SR2 собраны под 0.6+/0.7 и в " + V3(L) + " не загрузятся.",
                "Обновите MelonLoader до 0.7.x (Open-Beta)."));
        int unityMajor = int.TryParse(env.UnityVersion.Split('.')[0], out var um) ? um : 0;
        if (L != null && unityMajor >= 6000 && L < new Version(0, 7, 3))
            l.Add(Issue(CompatLevel.Warning, "loader-unity6", "MelonLoader < 0.7.3", "MelonLoader старее 0.7.3 для Unity 6",
                $"Игра на Unity {env.UnityVersion}. Загрузку AssetBundle и картинок (Il2CppAssetBundleManager, ImageConversion) для Unity 6000+ исправили только в MelonLoader 0.7.3 — моды с текстурами и бандлами могут не работать.",
                "Обновите MelonLoader до 0.7.3 или новее."));
        if (env.Runtime == "net6" || File.Exists(IOPath.Combine(env.GameDir, "MelonLoader", "net6", "MelonLoader.dll")))
        {
            if (env.DotNet == null)
                l.Add(Issue(CompatLevel.Error, "dotnet-missing", "нет .NET 6", "Не найден .NET 6 Runtime",
                    "MelonLoader " + env.LoaderShort + " запускает моды на .NET 6 (MelonLoader.runtimeconfig.json: net6.0, " + (env.RollForward.Length > 0 ? env.RollForward : "Minor") + "), но подходящего рантайма нет"
                    + (env.DotNetInstalled.Count > 0 ? " (установлены: " + string.Join(", ", env.DotNetInstalled) + ")." : "."),
                    "Установите .NET 6 Desktop Runtime x64 с сайта Microsoft."));
            if (env.Il2CppInterop == null)
                l.Add(Issue(CompatLevel.Error, "interop-missing", "нет Il2CppInterop", "Нет Il2CppInterop", "В MelonLoader\\net6 нет Il2CppInterop.Runtime.dll — Il2Cpp-моды не смогут обращаться к игре.", "Переустановите MelonLoader."));
            if (env.Harmony == null)
                l.Add(Issue(CompatLevel.Error, "harmony-missing", "нет Harmony", "Нет 0Harmony.dll", "В MelonLoader\\net6 нет 0Harmony.dll — моды с патчами Harmony не загрузятся.", "Переустановите MelonLoader."));
            if (!env.Tomlet)
                l.Add(Issue(CompatLevel.Error, "tomlet-missing", "нет Tomlet", "MelonPreferences не работают", "В MelonLoader\\net6 нет Tomlet.dll — настройки модов (MelonPreferences.cfg) не загрузятся.", "Переустановите MelonLoader."));
            if (!env.SupportModule)
                l.Add(Issue(CompatLevel.Error, "support-missing", "нет модуля Il2Cpp", "Нет модуля поддержки Il2Cpp", "Нет MelonLoader\\Dependencies\\SupportModules\\Il2Cpp.dll — OnUpdate/OnGUI у модов вызываться не будут.", "Переустановите MelonLoader."));
        }
        if (env.GameAssemblies <= 0)
            l.Add(Issue(CompatLevel.Info, "game-asm-missing", "сборки не созданы", "Сборки игры ещё не сгенерированы",
                "В MelonLoader\\Il2CppAssemblies пусто — MelonLoader создаст их при первом запуске игры (это занимает несколько минут). До этого ссылки модов на код игры проверить нельзя."));
        else if (env.GeneratorStale)
            l.Add(Issue(CompatLevel.Warning, "game-asm-stale", "игра обновилась", "Сборки игры устарели",
                "MelonLoader: " + env.GeneratorNote + ". При следующем запуске он перегенерирует Il2CppAssemblies (первый запуск будет дольше), а моды, собранные под прошлую версию SR2, могут сломаться.",
                "После запуска проверьте журнал (кнопка «Логи»); обновите моды с ошибками MissingMethod/TypeLoad."));
        if (env.PrefsError.Length > 0)
            l.Add(Issue(CompatLevel.Warning, "prefs-broken", "MelonPreferences.cfg", "Файл настроек модов повреждён",
                "UserData\\MelonPreferences.cfg: " + env.PrefsError + ". Tomlet не сможет его прочитать — MelonLoader сбросит настройки модов к стандартным.",
                "Исправьте строку в MelonPreferences.cfg (меню ⋯) или удалите файл, чтобы он создался заново."));
        return l;
    }

    /// Прочитать манифесты установленных модов (кэш по времени файла).
    public static Dictionary<string, ModManifest> ReadManifests(IEnumerable<ModEntry> mods)
    {
        var d = new Dictionary<string, ModManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mods)
            if (!m.Available && !string.IsNullOrEmpty(m.Path) && File.Exists(m.Path)) d[m.Id] = ManifestReader.Read(m.Path);
        return d;
    }

    public static CompatReport Check(IReadOnlyList<ModEntry> mods, IReadOnlyDictionary<string, ModManifest> manifests, Func<string, bool> isOn, LoaderEnv env, MelonLogInfo? log)
    {
        var sw = Stopwatch.StartNew();
        var rep = new CompatReport { Env = env, Manifests = manifests };
        var modNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mods)
        {
            if (m.Available) continue;
            modNames.Add(m.Id);
            if (!string.IsNullOrEmpty(m.AssemblyName)) modNames.Add(m.AssemblyName);
            if (!string.IsNullOrEmpty(m.Name)) modNames.Add(m.Name);
        }
        foreach (var m in mods)
        {
            if (m.Available || !manifests.TryGetValue(m.Id, out var mf) || !mf.Readable) continue;
            var list = PerMod(m, mf, env, log, modNames);
            if (list.Count > 0) rep.ByMod[m.Id] = list;
        }
        CrossMod(rep, mods, manifests, isOn);
        foreach (var l in rep.ByMod.Values) l.Sort((a, b) => b.Level.CompareTo(a.Level));
        rep.Took = sw.Elapsed;
        return rep;
    }

    private static void Add(CompatReport rep, string id, CompatIssue i)
    {
        if (!rep.ByMod.TryGetValue(id, out var l)) rep.ByMod[id] = l = new List<CompatIssue>();
        if (l.Any(x => x.Code == i.Code && x.Badge == i.Badge)) return;
        l.Add(i);
    }

    private static bool IsGameAsm(string n) =>
        n.StartsWith("Il2Cpp", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("Il2CppInterop", StringComparison.OrdinalIgnoreCase)
        || n.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) || n.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase)
        || n.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase) || n.StartsWith("MonomiPark", StringComparison.OrdinalIgnoreCase);

    private static bool IsFrameworkName(string n) =>
        n is "mscorlib" or "netstandard" or "WindowsBase" || n.StartsWith("System", StringComparison.Ordinal) || n.StartsWith("Microsoft.", StringComparison.Ordinal);

    private static readonly HashSet<string> DesktopOnly = new(StringComparer.OrdinalIgnoreCase)
    { "System.Windows.Forms", "System.Drawing", "System.Drawing.Common", "PresentationCore", "PresentationFramework", "WindowsBase", "System.Xaml", "System.Windows.Forms.Primitives" };

    public static List<CompatIssue> PerMod(ModEntry m, ModManifest mf, LoaderEnv env, MelonLogInfo? log, ISet<string> modNames)
    {
        var l = new List<CompatIssue>();
        var L = env.Loader;
        string Lt = env.LoaderText.Length > 0 ? env.LoaderText : L != null ? V3(L) : "";
        var ls = log?.For(m.Id);
        bool cleanInLog = log != null && log.WasLoaded(m.Id) && (ls == null || ls.Errors == 0);

        if (!mf.IsMelon)
        {
            l.Add(Issue(CompatLevel.Warning, "not-melon", "не мод", "Это библиотека, а не мод",
                $"В {m.FileName} нет атрибута MelonInfo — это обычная .NET-библиотека. MelonLoader ищет библиотеки в UserLibs, а из Mods попробует загрузить её как мод и запишет ошибку в журнал.",
                "Переместите DLL в папку UserLibs (или установите её перетаскиванием — менеджер разложит сам)."));
            return l;
        }
        if (mf.LegacyInfo)
            l.Add(Issue(CompatLevel.Error, "legacy-info", "очень старый мод", "Мод для MelonLoader 0.2–0.3",
                "Мод описан старым атрибутом MelonModInfo/MelonPluginInfo — так делали до MelonLoader 0.4. В " + Lt + " он не загрузится.",
                "Найдите версию мода для MelonLoader 0.6+."));
        bool oldInterop = mf.UsesUnhollower || (mf.LoaderRef != null && mf.LoaderRef < new Version(0, 6, 0));
        if (oldInterop)
            l.Add(Issue(CompatLevel.Error, "unhollower", "для MelonLoader 0.5", "Собран для MelonLoader 0.5 (Unhollower)",
                (mf.UsesUnhollower ? "Мод ссылается на UnhollowerBaseLib — " : $"Мод собран против MelonLoader {V3(mf.LoaderRef!)} — ")
                + "это MelonLoader до 0.6. С 0.6 Unhollower заменили на Il2CppInterop, а моды запускаются на .NET 6, поэтому в " + Lt + " такой мод не загрузится.",
                "Нужна версия мода для MelonLoader 0.6+ (обычно у автора есть обновление)."));
        else if (mf.LoaderRef != null && L != null)
        {
            var R = mf.LoaderRef;
            if (R.Major > L.Major || R.Major == L.Major && R.Minor > L.Minor)
                l.Add(Issue(CompatLevel.Error, "loader-newer", "нужен ML " + V2(R), "Нужен MelonLoader " + V3(R),
                    $"Мод собран для MelonLoader {V3(R)}, а установлен {Lt}. Он может вызывать функции, которых в {V2(L)} ещё нет, и упадёт при загрузке.",
                    $"Обновите MelonLoader до {V2(R)}.x или возьмите версию мода для {V2(L)}."));
            else if (R.Major == L.Major && R.Minor == L.Minor && Math.Max(0, R.Build) > Math.Max(0, L.Build))
                l.Add(Issue(CompatLevel.Warning, "loader-newer-patch", "ML " + V3(R) + "+", "Собран для более нового MelonLoader",
                    $"Мод собран для MelonLoader {V3(R)}, установлен {Lt}. Обычно это работает, но если в журнале будут MissingMethodException — обновите MelonLoader.",
                    $"Обновите MelonLoader до {V3(R)} или новее."));
            else if (R.Major == L.Major && R.Minor < L.Minor && !cleanInLog)
            {
                if (ls != null && ls.Outdated)
                    l.Add(Issue(CompatLevel.Error, "loader-older-broken", "для ML " + V2(R), "Не работает в MelonLoader " + V2(L),
                        $"Мод собран для MelonLoader {V3(R)}, а в журнале прошлого запуска: {ls.OutdatedReason}. В {V2(L)}.x часть API изменилась, и эта версия мода с ней не совместима.",
                        "Нужна версия мода, собранная под MelonLoader " + V2(L) + "."));
                else
                    l.Add(Issue(CompatLevel.Info, "loader-older", "собран для ML " + V2(R), "Собран для MelonLoader " + V2(R) + ".x",
                        $"Установлен {Lt}. Моды для {V2(R)}.x в {V2(L)}.x обычно работают (тот же .NET 6 и Il2CppInterop), но часть API изменилась — если в журнале появятся ошибки MissingMethod/TypeLoad, нужна новая версия мода."));
            }
        }
        if (mf.RequiredLoader.Length > 0 && L != null && Version.TryParse(Pad3(mf.RequiredLoader), out var req))
        {
            var have = new Version(L.Major, L.Minor, Math.Max(0, L.Build));
            var need = new Version(req.Major, req.Minor, Math.Max(0, req.Build));
            bool ok = mf.RequiredIsMinimum ? have >= need : have == need;
            if (!ok)
                l.Add(Issue(CompatLevel.Error, "verify-loader", (mf.RequiredIsMinimum ? "ML " + V3(need) + "+" : "только ML " + V3(need)), "Требует MelonLoader " + V3(need) + (mf.RequiredIsMinimum ? " или новее" : ""),
                    $"Автор указал VerifyLoaderVersion({V3(need)}{(mf.RequiredIsMinimum ? ", минимум" : "")}) — MelonLoader {Lt} откажется загружать этот мод.",
                    mf.RequiredIsMinimum ? $"Обновите MelonLoader до {V3(need)}+" : $"Нужен ровно MelonLoader {V3(need)} или другая версия мода."));
        }
        if (mf.Games.Count > 0 && (env.GameName.Length > 0 || env.GameDeveloper.Length > 0))
        {
            bool match = mf.Games.Any(gm =>
                (string.IsNullOrEmpty(gm.Developer) || Norm(gm.Developer) == Norm(env.GameDeveloper)) &&
                (string.IsNullOrEmpty(gm.Name) || Norm(gm.Name) == Norm(env.GameName)));
            if (!match)
            {
                var gm = mf.Games[0];
                string target = string.Join(" ", new[] { gm.Developer, gm.Name }.Where(s => !string.IsNullOrEmpty(s)));
                l.Add(Issue(CompatLevel.Error, "other-game", "для другой игры", "Мод для другой игры",
                    $"Мод помечен атрибутом MelonGame для «{target}», а это {env.GameDeveloper} {env.GameName}. MelonLoader не загрузит его в Slime Rancher 2.",
                    "Удалите его из Mods или найдите версию для Slime Rancher 2."));
            }
        }
        if (mf.Domain == 1)
            l.Add(Issue(CompatLevel.Error, "mono-only", "только Mono", "Мод для Mono-игр",
                "Мод помечен MelonPlatformDomain(MONO), а Slime Rancher 2 — Il2Cpp-игра. MelonLoader его не загрузит.", "Нужна Il2Cpp-версия мода."));
        if (mf.Platforms.Count > 0 && !mf.Platforms.Contains(0) && !mf.Platforms.Contains(2))
            l.Add(Issue(CompatLevel.Error, "platform", mf.Platforms.Contains(1) ? "только x86" : "другая платформа", "Не для Windows x64",
                "MelonPlatform мода: " + string.Join(", ", mf.Platforms.Select(p => p switch { 1 => "Windows x86", 2 => "Windows x64", _ => "платформа " + p })) + ". Slime Rancher 2 — 64-битная игра для Windows.", "Нужна x64-версия мода."));
        if (mf.Processes.Count > 0)
        {
            string exe = IOPath.GetFileNameWithoutExtension(ModStore.GameProcessName + ".exe");
            if (!mf.Processes.Any(p => IOPath.GetFileNameWithoutExtension(p).Equals(exe, StringComparison.OrdinalIgnoreCase)))
                l.Add(Issue(CompatLevel.Error, "process", "другой процесс", "Мод для другого .exe",
                    "MelonProcess мода: " + string.Join(", ", mf.Processes) + " — MelonLoader загрузит его только в этом процессе, а не в SlimeRancher2.exe."));
        }
        if (mf.GameVersions.Count > 0 && env.GameVersion.Length > 0 &&
            !mf.GameVersions.Any(v => v.Equals(env.GameVersion, StringComparison.OrdinalIgnoreCase) || v.Equals(env.ShortGameVersion, StringComparison.OrdinalIgnoreCase)))
            l.Add(Issue(CompatLevel.Error, "game-version", "для SR2 " + mf.GameVersions[0], "Мод для другой версии игры",
                $"MelonGameVersion мода: {string.Join(", ", mf.GameVersions)}, а у вас SR2 {env.ShortGameVersion}. MelonLoader пропустит его при загрузке.",
                "Найдите версию мода для SR2 " + env.ShortGameVersion + "."));

        // .NET: под что собран мод и на чём его запустит MelonLoader
        bool dotnetFlagged = false;
        var fv = mf.FrameworkVersion;
        if (mf.FrameworkKind == "core" && fv != null && env.DotNet != null && fv.Major > env.DotNet.Major)
        {
            dotnetFlagged = true;
            l.Add(Issue(CompatLevel.Error, "dotnet-newer", ".NET " + fv.Major, "Собран под .NET " + fv.ToString(2),
                $"MelonLoader {Lt} запускает моды на .NET {env.DotNet} (MelonLoader.runtimeconfig.json), а мод требует .NET {fv.ToString(2)} — сборка не загрузится (System.Runtime {fv.Major}.0).",
                "Нужна версия мода под net6.0."));
        }
        else if (mf.FrameworkKind == "framework" && !oldInterop)
            l.Add(Issue(CompatLevel.Warning, "netfx", ".NET Framework", "Собран под " + mf.FrameworkLabel,
                "Так собирали моды для MelonLoader 0.5 и Mono-игр. MelonLoader 0.6+ запускает Il2Cpp-моды на .NET 6 — такая сборка может не загрузиться или упасть на первом вызове.",
                "Поищите версию мода под net6.0."));

        // ссылки на сборки: всё ли есть (рантайм, MelonLoader, сборки игры, UserLibs, другие моды)
        var missingGame = new List<string>();
        foreach (var (name, need) in mf.References)
        {
            if (name.Equals(mf.AssemblyName, StringComparison.OrdinalIgnoreCase) || modNames.Contains(name)) continue; // зависимость от мода — проверяется отдельно
            if (mf.Optional.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (name.StartsWith("Unhollower", StringComparison.OrdinalIgnoreCase) || name.Equals("MelonLoader", StringComparison.OrdinalIgnoreCase) && !env.LoaderInstalled) continue;
            string where = env.WhereOf(name);
            if (where.Length > 0)
            {
                if (where == "framework")
                {
                    if (!dotnetFlagged && env.DotNet != null && need.Major > env.DotNet.Major && need.Major < 100)
                    {
                        dotnetFlagged = true;
                        l.Add(Issue(CompatLevel.Error, "dotnet-newer", ".NET " + need.Major, "Нужен .NET " + need.Major,
                            $"Мод ссылается на {name} {V3(need)}, а MelonLoader запускает моды на .NET {env.DotNet}. Сборка не загрузится.", "Нужна версия мода под net6.0."));
                    }
                    continue;
                }
                if (where == "game") continue; // у сгенерированных сборок версия 0.0.0.0
                var have = env.VersionOf(name);
                if (have == null) continue;
                bool lower = have.Major < need.Major || have.Major == need.Major && have.Minor < need.Minor;
                if (!lower) continue;
                if (name.StartsWith("Il2CppInterop", StringComparison.OrdinalIgnoreCase))
                    l.Add(Issue(CompatLevel.Warning, "interop-older", "Il2CppInterop " + V2(need), "Нужен Il2CppInterop " + V3(need),
                        $"Мод собран с Il2CppInterop {V3(need)}, а в MelonLoader {Lt} — {V3(have)}. Возможны ошибки MissingMethod при обращении к игре.",
                        "Обновите MelonLoader."));
                else if (name.Equals("0Harmony", StringComparison.OrdinalIgnoreCase))
                    l.Add(Issue(CompatLevel.Warning, "harmony-older", "Harmony " + V2(need), "Нужен Harmony " + V3(need),
                        $"Мод собран с Harmony {V3(need)}, а в MelonLoader {Lt} — {V3(have)}. Патчи могут не примениться.", "Обновите MelonLoader."));
                else
                    l.Add(Issue(CompatLevel.Warning, "lib-older", name + " " + V2(need), "Старая версия " + name,
                        $"Мод ждёт {name} {V3(need)}, а в {where} лежит {V3(have)}.", $"Положите {name}.dll версии {V3(need)}+ в UserLibs."));
                continue;
            }
            if (IsFrameworkName(name))
            {
                if (env.DotNetDir.Length == 0) continue; // рантайм не найден — об этом уже сказано
                if (DesktopOnly.Contains(name))
                    l.Add(Issue(CompatLevel.Warning, "desktop-lib", name, "Нужен " + name,
                        $"Мод ссылается на {name} — это часть .NET Desktop (WinForms/WPF), а MelonLoader подключает только Microsoft.NETCore.App. Окна мода могут не открыться.",
                        "Если мод падает при открытии своих окон — свяжитесь с автором."));
                else if (!name.StartsWith("Microsoft.", StringComparison.Ordinal))
                    l.Add(Issue(CompatLevel.Warning, "framework-missing", "нет " + name, "Нет системной библиотеки " + name,
                        $"Мод ссылается на {name} {V3(need)}, а в .NET {env.DotNet} такой сборки нет.", $"Положите {name}.dll в UserLibs."));
                continue;
            }
            if (IsGameAsm(name)) { missingGame.Add(name); continue; }
            l.Add(Issue(CompatLevel.Error, "missing-lib", "нет " + name, "Не хватает библиотеки " + name,
                $"Мод ссылается на {name} {V3(need)}, но её нет ни в UserLibs, ни в MelonLoader, ни среди модов. Мод упадёт при первом обращении к ней.",
                $"Положите {name}.dll в папку UserLibs (обычно она лежит в архиве с модом)."));
        }
        if (missingGame.Count > 0)
        {
            string names = string.Join(", ", missingGame.Take(4)) + (missingGame.Count > 4 ? $" и ещё {missingGame.Count - 4}" : "");
            if (env.GameAssemblies <= 0)
                l.Add(Issue(CompatLevel.Info, "game-asm-pending", "проверится после запуска", "Сборки игры ещё не созданы",
                    $"Мод ссылается на {names}. Эти сборки MelonLoader сгенерирует при первом запуске — тогда менеджер сможет их проверить."));
            else
                l.Add(Issue(CompatLevel.Error, "game-asm", "нет " + missingGame[0], missingGame.Count == 1 ? "Нет сборки игры " + missingGame[0] : "Нет сборок игры",
                    $"Мод ссылается на {names}, а среди сгенерированных сборок игры (MelonLoader\\Il2CppAssemblies) их нет. Скорее всего, мод собран для другой игры или другой версии SR2 / MelonLoader.",
                    "Найдите версию мода для текущей версии игры" + (env.GameVersion.Length > 0 ? " (" + env.ShortGameVersion + ")" : "") + "."));
        }
        if (mf.UsesPreferences && env.LoaderInstalled && env.Tomlet && env.PrefsError.Length > 0)
            l.Add(Issue(CompatLevel.Warning, "prefs", "настройки сбросятся", "MelonPreferences.cfg повреждён",
                $"Мод хранит настройки в MelonPreferences, а файл UserData\\MelonPreferences.cfg не читается ({env.PrefsError}). MelonLoader сбросит их к стандартным.",
                "Исправьте файл или удалите его, чтобы он создался заново."));
        return l;
    }

    private static string Pad3(string v)
    {
        var parts = v.Trim().TrimStart('v', 'V').Split('-', '+')[0].Split('.');
        return string.Join(".", parts.Concat(Enumerable.Repeat("0", Math.Max(0, 3 - parts.Length))).Take(4));
    }

    private static bool Related(ModEntry a, ModEntry b) =>
        a.DependsOn.Contains(b.Id, StringComparer.OrdinalIgnoreCase) || b.DependsOn.Contains(a.Id, StringComparer.OrdinalIgnoreCase);

    /// Проверки между включёнными модами: дубликаты, несовместимость, пересечения хуков Harmony.
    private static void CrossMod(CompatReport rep, IReadOnlyList<ModEntry> mods, IReadOnlyDictionary<string, ModManifest> mfs, Func<string, bool> isOn)
    {
        var on = mods.Where(m => !m.Available && isOn(m.Id) && mfs.TryGetValue(m.Id, out var f) && f.Readable).ToList();
        if (on.Count < 2) return;

        foreach (var g in on.GroupBy(m => mfs[m.Id].AssemblyName, StringComparer.OrdinalIgnoreCase).Where(g => g.Key.Length > 0 && g.Count() > 1))
            foreach (var m in g)
            {
                var others = g.Where(o => o != m).ToList();
                var i = Issue(CompatLevel.Error, "duplicate", "дубликат", "Две копии одного мода",
                    $"{m.FileName} и {string.Join(", ", others.Select(o => o.FileName))} — одна и та же сборка {g.Key}. MelonLoader загрузит только одну, вторая выдаст ошибку, а настройки могут перепутаться.",
                    "Оставьте включённым один файл (лишний можно удалить в корзину).");
                i.Related.AddRange(others.Select(o => o.Id));
                Add(rep, m.Id, i);
            }
        foreach (var g in on.Where(m => mfs[m.Id].MelonName.Length > 0).GroupBy(m => mfs[m.Id].MelonName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            foreach (var m in g)
            {
                if (rep.For(m.Id).Any(x => x.Code == "duplicate")) continue;
                var others = g.Where(o => o != m).ToList();
                var i = Issue(CompatLevel.Warning, "same-name", "одинаковое имя", "Два мода с одним именем",
                    $"У {m.FileName} и {string.Join(", ", others.Select(o => o.FileName))} одинаковое имя «{g.Key}» в MelonInfo. Похоже на две версии одного мода — их настройки в MelonPreferences смешаются.",
                    "Оставьте включённым один.");
                i.Related.AddRange(others.Select(o => o.Id));
                Add(rep, m.Id, i);
            }

        foreach (var m in on)
            foreach (var inc in mfs[m.Id].Incompatible)
                foreach (var o in on)
                {
                    if (o == m) continue;
                    var of = mfs[o.Id];
                    if (!(Eq(of.AssemblyName, inc) || Eq(of.MelonName, inc) || Eq(o.Id, inc))) continue;
                    var i = Issue(CompatLevel.Error, "incompatible", "несовместим с " + o.DisplayName, "Несовместим с " + o.DisplayName,
                        $"Автор {m.DisplayName} пометил его несовместимым с {o.DisplayName} (MelonIncompatibleAssemblies). Вместе они не заработают — MelonLoader не загрузит {m.DisplayName}.",
                        $"Выключите {m.DisplayName} или {o.DisplayName}.");
                    i.Related.Add(o.Id);
                    Add(rep, m.Id, i);
                    var j = Issue(CompatLevel.Error, "incompatible", "несовместим с " + m.DisplayName, "Несовместим с " + m.DisplayName,
                        $"{m.DisplayName} помечен автором как несовместимый с этим модом (MelonIncompatibleAssemblies).", $"Выключите {m.DisplayName} или {o.DisplayName}.");
                    j.Related.Add(m.Id);
                    Add(rep, o.Id, j);
                }

        // Harmony: одни и те же методы игры у разных модов
        var byKey = new Dictionary<string, List<(ModEntry Mod, HookTarget Hook)>>(StringComparer.Ordinal);
        foreach (var m in on)
            foreach (var h in mfs[m.Id].Hooks)
                if (!h.Dynamic)
                {
                    if (!byKey.TryGetValue(h.Key, out var lst)) byKey[h.Key] = lst = new();
                    lst.Add((m, h));
                }
        foreach (var m in on)
        {
            var mine = mfs[m.Id].Hooks.Where(h => !h.Dynamic).GroupBy(h => h.Key).ToList();
            var shared = new List<(string Key, List<HookTarget> Mine, List<(ModEntry Mod, HookTarget Hook)> Theirs)>();
            foreach (var g in mine)
            {
                var theirs = byKey[g.Key].Where(x => x.Mod != m).ToList();
                if (theirs.Count > 0) shared.Add((g.Key, g.ToList(), theirs));
            }
            if (shared.Count == 0) continue;
            var others = shared.SelectMany(s => s.Theirs.Select(t => t.Mod)).Distinct().ToList();
            bool related = others.All(o => Related(m, o));
            bool risky = shared.Any(s => s.Theirs.Any(t => !Related(m, t.Mod) && (t.Hook.Kind == "Transpiler" || t.Hook.SkipsOriginal))
                                      || s.Mine.Any(h => h.Kind == "Transpiler" || h.SkipsOriginal) && s.Theirs.Any(t => !Related(m, t.Mod)));
            var lines = shared.Take(4).Select(s => s.Mine[0].Short + " — у " + string.Join(", ", s.Theirs.Select(t => t.Mod.DisplayName + " (" + t.Hook.KindText + ")").Distinct())).ToList();
            string list = string.Join("; ", lines) + (shared.Count > 4 ? $"; и ещё {shared.Count - 4}" : "");
            string names = string.Join(", ", others.Select(o => o.DisplayName));
            var i = risky
                ? Issue(CompatLevel.Warning, "hook-conflict", "конфликт хуков", "Конфликт хуков Harmony с " + names,
                    $"Патчит те же методы игры, что и {names}: {list}. Префикс с отменой (возвращает false) или транспайлер одного мода может отменить или переписать изменения другого — что сработает, зависит от порядка патчей.",
                    "Если что-то из этих модов перестало работать — выключите один из них и проверьте журнал MelonLoader.")
                : Issue(CompatLevel.Info, "hook-shared", "общие хуки: " + shared.Count, "Общие хуки Harmony с " + names,
                    $"Патчит те же методы игры, что и {names}: {list}. " + (related ? "Моды связаны зависимостью — скорее всего, так задумано." : "Оба мода только добавляют свой код до или после метода — обычно это безопасно."));
            i.Related.AddRange(others.Select(o => o.Id));
            Add(rep, m.Id, i);
        }
    }

    /// Короткие факты о сборке мода для подробностей: под какой MelonLoader, .NET, сколько хуков.
    public static List<string> Facts(ModManifest mf)
    {
        var f = new List<string>();
        if (mf.LoaderRef != null) f.Add("собран для ML " + V3(mf.LoaderRef));
        if (mf.FrameworkLabel.Length > 0) f.Add(mf.FrameworkLabel);
        if (mf.References.TryGetValue("Il2CppInterop.Runtime", out var iv)) f.Add("Il2CppInterop " + V3(iv));
        if (mf.Hooks.Count > 0)
        {
            int targets = mf.Hooks.Where(h => !h.Dynamic).Select(h => h.Key).Distinct().Count();
            f.Add("Harmony: " + targets + " " + Plural(targets, "метод", "метода", "методов") + (mf.Hooks.Any(h => h.Dynamic) ? " + вычисляемые" : "")
                  + (mf.Hooks.All(h => h.Manual) ? " (патчи в коде)" : ""));
        }
        else if (mf.UsesHarmony) f.Add(mf.ManualPatching ? "Harmony: патчи в коде" : "Harmony");
        if (mf.UsesPreferences) f.Add("MelonPreferences");
        if (mf.Additional.Count > 0) f.Add("зависит: " + string.Join(", ", mf.Additional));
        if (mf.Optional.Count > 0) f.Add("необязательно: " + string.Join(", ", mf.Optional));
        if (mf.Priority != 0) f.Add("приоритет " + mf.Priority);
        return f;
    }

    private static string Plural(int n, string one, string few, string many)
    {
        int a = Math.Abs(n) % 100, b = a % 10;
        return a is >= 11 and <= 14 ? many : b == 1 ? one : b is >= 2 and <= 4 ? few : many;
    }
}
