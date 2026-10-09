using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Modules.Runtime;

// Compiled into each module: the host does not learn any game layouts or names.
internal sealed class Il2CppRuntimeResolver : IDisposable
{
    private readonly Process _process;
    private readonly IntPtr _handle;
    private readonly DateTime _start;
    private readonly Dictionary<string, ulong> _exports;
    private readonly ConcurrentDictionary<string, ulong> _classes;
    private readonly ConcurrentDictionary<string, ulong> _fields;
    private readonly ConcurrentDictionary<string, NativeMethod> _methods;
    private readonly List<ulong> _images = [];
    private readonly Dictionary<ulong, string> _imageNames = new();
    private readonly ConcurrentDictionary<ulong, string> _listItemTypes;
    private readonly ConcurrentDictionary<ulong, Dictionary<string, List<ulong>>> _methodNames;
    private static readonly ConcurrentDictionary<string, MetadataCache> MetadataCaches = new();
    private sealed class MetadataCache
    {
        internal readonly ConcurrentDictionary<string, ulong> Classes = new();
        internal readonly ConcurrentDictionary<string, ulong> Fields = new();
        internal readonly ConcurrentDictionary<string, NativeMethod> Methods = new();
        internal readonly ConcurrentDictionary<ulong, string> ListItemTypes = new();
        internal readonly ConcurrentDictionary<ulong, Dictionary<string, List<ulong>>> MethodNames = new();
    }
    private bool _uncertain;
    public ulong ModuleBase { get; }
    public ulong ModuleEnd { get; }

    public Il2CppRuntimeResolver(GameProcessContext context)
    {
        _process = Process.GetProcessById(context.ProcessId);
        _start = context.StartTimeUtc.ToUniversalTime();
        _handle = OpenProcess(0x43A, false, context.ProcessId);
        if (_handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法连接游戏进程。");
        try
        {
            CheckAlive();
            if (!SamePath(_process.MainModule?.FileName, context.ExecutablePath))
                throw new InvalidOperationException("游戏运行路径已变化，请重新连接。");
            var module = _process.Modules.Cast<ProcessModule>().SingleOrDefault(m =>
                m.ModuleName.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("游戏尚未加载 GameAssembly.dll，请稍后刷新。");
            if (!SamePath(module.FileName, Path.Combine(Path.GetDirectoryName(context.ExecutablePath)!, "GameAssembly.dll")))
                throw new InvalidOperationException("游戏加载的 GameAssembly 路径不一致，请重新连接。");
            ModuleBase = unchecked((ulong)module.BaseAddress.ToInt64());
            ModuleEnd = ModuleBase + (ulong)module.ModuleMemorySize;
            if (MetadataCaches.Count > 8) MetadataCaches.Clear();
            var cache = MetadataCaches.GetOrAdd($"{context.ProcessId}|{_start.Ticks}|{ModuleBase:X}|{ModuleEnd:X}|{module.FileName}", _ => new());
            _classes = cache.Classes; _fields = cache.Fields; _methods = cache.Methods;
            _listItemTypes = cache.ListItemTypes; _methodNames = cache.MethodNames;
            _exports = ReadExports(module.FileName).ToDictionary(p => p.Key, p => ModuleBase + p.Value);
        }
        catch { CloseHandle(_handle); _process.Dispose(); throw; }
    }

    public static bool IsNamedGame(GameProcessContext process, params string[] names)
    {
        static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var aliases = names.Select(name => Normalize(Path.GetFileNameWithoutExtension(name))).ToHashSet();
        if (aliases.Contains(Normalize(Path.GetFileNameWithoutExtension(process.ProcessName)))) return true;
        // Renamed executables can still expose the game's own window/product name.
        // Never infer identity from a directory, a partial name, or an old file hash.
        try
        {
            using var live = Process.GetProcessById(process.ProcessId);
            if (live.StartTime.ToUniversalTime() != process.StartTimeUtc.ToUniversalTime() ||
                !SamePath(live.MainModule?.FileName, process.ExecutablePath)) return false;
            return aliases.Contains(Normalize(live.MainWindowTitle)) ||
                aliases.Contains(Normalize(FileVersionInfo.GetVersionInfo(process.ExecutablePath).ProductName ?? ""));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception or IOException) { return false; }
    }

    public void CheckAlive()
    {
        if (!GetProcessTimes(_handle, out var creation, out _, out _, out _) ||
            DateTime.FromFileTimeUtc(creation) != _start || _process.HasExited)
            throw new InvalidOperationException("游戏进程已退出或重新启动，请重新连接。");
    }

    public ulong Export(string name) => _exports.TryGetValue(name, out var pointer) ? pointer :
        throw new InvalidOperationException($"游戏运行时缺少 {name} 接口。");

    public ulong Class(string imageName, string ns, string name)
    {
        var key = $"{imageName}|{ns}|{name}";
        if (_classes.TryGetValue(key, out var cached)) return cached;
        if (_images.Count == 0)
        {
            using var count = Allocate(8);
            var assemblies = Call(Export("il2cpp_domain_get_assemblies"), Call(Export("il2cpp_domain_get")), count.Address);
            var length = ReadPointer(count.Address);
            if (length is 0 or > 1024 || assemblies == 0) throw new InvalidOperationException("游戏运行时尚未初始化，请稍后刷新。");
            for (ulong i = 0; i < length; i++)
            {
                var image = Call(Export("il2cpp_assembly_get_image"), ReadPointer(assemblies + i * 8));
                _images.Add(image);
                _imageNames[image] = ReadUtf8(Call(Export("il2cpp_image_get_name"), image));
            }
        }
        using var namespaceText = Utf8(ns);
        using var nameText = Utf8(name);
        var matches = new List<ulong>();
        foreach (var image in _images)
        {
            if (!_imageNames[image].Equals(imageName, StringComparison.Ordinal)) continue;
            var klass = Call(Export("il2cpp_class_from_name"), image, namespaceText.Address, nameText.Address);
            if (klass != 0) matches.Add(klass);
        }
        // Game updates can split managed code into another assembly without changing
        // the fully-qualified type. Keep that identity, never guess another namespace.
        if (matches.Count == 0)
        {
            foreach (var image in _images.Where(image => _imageNames[image] != imageName))
            {
                var klass = Call(Export("il2cpp_class_from_name"), image, namespaceText.Address, nameText.Address);
                if (klass != 0) matches.Add(klass);
            }
        }
        if (matches.Count != 1) throw new InvalidOperationException($"当前游戏找不到唯一类型 {ns}.{name}（{imageName}）。");
        return _classes[key] = matches[0];
    }

    public ulong ObjectClass(ulong instance) => instance != 0 ? ReadPointer(instance) :
        throw new InvalidOperationException("当前存档对象尚未加载，请进入存档后刷新。");

    public ulong NestedClass(ulong parent, string name)
    {
        using var iterator = Allocate(8);
        var matches = new List<ulong>();
        for (var i = 0; i < 1024; i++)
        {
            var child = Call(Export("il2cpp_class_get_nested_types"), parent, iterator.Address);
            if (child == 0) break;
            if (ReadUtf8(Call(Export("il2cpp_class_get_name"), child)) == name) matches.Add(child);
        }
        return matches.Count == 1 ? matches[0] : throw new InvalidOperationException($"当前游戏找不到唯一嵌套类型 {name}。");
    }

    public string TypeName(ulong type)
    {
        var text = Call(Export("il2cpp_type_get_name"), type);
        try { return ReadUtf8(text); }
        finally { if (text != 0) Call(Export("il2cpp_free"), text); }
    }

    public ulong Field(ulong klass, string name, string typeName)
    {
        var key = $"{klass:X}|{name}|{typeName}";
        if (_fields.TryGetValue(key, out var cached)) return cached;
        using var text = Utf8(name);
        var field = Call(Export("il2cpp_class_get_field_from_name"), klass, text.Address);
        if (field == 0) throw new InvalidOperationException($"当前游戏字段 {name} 已变化，无法定位该项。");
        var actual = TypeName(Call(Export("il2cpp_field_get_type"), field));
        if (!SameType(actual, typeName)) throw new InvalidOperationException($"当前游戏字段 {name} 类型为 {actual}，预期 {typeName}；未执行修改。");
        var flags = Call(Export("il2cpp_field_get_flags"), field);
        if ((flags & 0x10) != 0) throw new InvalidOperationException($"字段 {name} 不是实例字段。");
        var offset = Call(Export("il2cpp_field_get_offset"), field);
        if (offset is < 0x10 or > 0x10000) throw new InvalidDataException($"字段 {name} 偏移无效。");
        return _fields[key] = offset;
    }

    public ulong Address(ulong instance, string name, string typeName) => checked(instance + Field(ObjectClass(instance), name, typeName));
    public ulong Reference(ulong instance, string name, string typeName) => ReadPointer(Address(instance, name, typeName));
    public ulong StaticReference(ulong klass, string name, string typeName)
    {
        var field = FindField(klass, name);
        if (!SameType(TypeName(Call(Export("il2cpp_field_get_type"), field)), typeName) ||
            (Call(Export("il2cpp_field_get_flags"), field) & 0x10) == 0)
            throw new InvalidOperationException($"静态字段 {name} 的类型已变化。");
        using var result = Allocate(8);
        Call(Export("il2cpp_field_static_get_value"), field, result.Address);
        return ReadPointer(result.Address);
    }

    public string FieldType(ulong klass, string name) => TypeName(Call(Export("il2cpp_field_get_type"), FindField(klass, name)));

    public NativeMethod Method(ulong klass, string name, bool isStatic, string returnType, params string[] parameters)
    {
        var key = $"{klass:X}|{name}|{isStatic}|{returnType}|{string.Join('|', parameters)}";
        if (_methods.TryGetValue(key, out var cached)) return cached;
        var matches = new List<NativeMethod>();
        if (!_methodNames.TryGetValue(klass, out var names))
        {
            names = new Dictionary<string, List<ulong>>();
            using var iterator = Allocate(8);
            for (var i = 0; i < 4096; i++)
            {
                var info = Call(Export("il2cpp_class_get_methods"), klass, iterator.Address);
                if (info == 0) break;
                var methodName = ReadUtf8(Call(Export("il2cpp_method_get_name"), info));
                if (!names.TryGetValue(methodName, out var infos)) names[methodName] = infos = [];
                infos.Add(info);
                if (i == 4095) throw new InvalidDataException("当前类型的方法数量超出解析上限。");
            }
            _methodNames[klass] = names;
        }
        foreach (var info in names.GetValueOrDefault(name, []))
        {
            if (Call(Export("il2cpp_method_get_param_count"), info) != (ulong)parameters.Length) continue;
            using var implementationFlags = Allocate(4);
            var flags = Call(Export("il2cpp_method_get_flags"), info, implementationFlags.Address);
            if (((flags & 0x10) != 0) != isStatic) continue;
            if (!SameType(TypeName(Call(Export("il2cpp_method_get_return_type"), info)), returnType)) continue;
            var types = Enumerable.Range(0, parameters.Length).Select(index =>
                TypeName(Call(Export("il2cpp_method_get_param"), info, (ulong)index))).ToArray();
            if (!types.Zip(parameters).All(pair => SameType(pair.First, pair.Second))) continue;
            var pointer = ReadPointer(info);
            if (pointer < ModuleBase || pointer >= ModuleEnd) throw new InvalidDataException($"方法 {name} 不在当前 GameAssembly 内。");
            matches.Add(new NativeMethod(pointer, info));
        }
        if (matches.Count != 1) throw new InvalidOperationException($"当前游戏找不到唯一方法 {name}({string.Join(", ", parameters)})；未执行修改。");
        return _methods[key] = matches[0];
    }

    public NativeMethod Method(string image, string ns, string klass, string name, bool isStatic, string result, params string[] parameters) =>
        Method(Class(image, ns, klass), name, isStatic, result, parameters);

    public static bool SameType(string actual, string expected)
    {
        static string Canonical(string value) => System.Text.RegularExpressions.Regex.Replace(
            value.Replace(" ", "", StringComparison.Ordinal).Replace('/', '.').Replace('+', '.'), "`[0-9]+", "");
        return Canonical(actual).Equals(Canonical(expected), StringComparison.Ordinal);
    }

    public ulong ListItems(ulong list)
    {
        var klass = ObjectClass(list);
        if (!_listItemTypes.TryGetValue(klass, out var type))
        {
            type = FieldType(klass, "_items");
            if (!type.EndsWith("[]", StringComparison.Ordinal)) throw new InvalidDataException("当前列表的元素缓冲区不是数组。");
            _listItemTypes[klass] = type;
        }
        return Reference(list, "_items", type);
    }
    private ulong FindField(ulong klass, string name)
    {
        using var text = Utf8(name);
        var field = Call(Export("il2cpp_class_get_field_from_name"), klass, text.Address);
        return field != 0 ? field : throw new InvalidOperationException($"当前集合缺少字段 {name}。");
    }
    public int ListCount(ulong list)
    {
        var count = ReadInt(Address(list, "_size", "System.Int32"));
        var array = ListItems(list);
        if (count < 0 || array == 0 || (ulong)count > ReadPointer(array + 0x18))
            throw new InvalidDataException("当前列表数量与元素容量不一致，请刷新后重试。");
        return count;
    }

    public byte[] Read(ulong address, int count)
    {
        CheckAlive();
        var bytes = new byte[count];
        if (!ReadProcessMemory(_handle, (IntPtr)(long)address, bytes, (nuint)count, out var read) || read != (nuint)count)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取当前游戏数据。");
        return bytes;
    }
    public ulong ReadPointer(ulong address) => BitConverter.ToUInt64(Read(address, 8));
    public int ReadInt(ulong address) => BitConverter.ToInt32(Read(address, 4));
    public string ReadUtf8(ulong address)
    {
        if (address == 0) return string.Empty;
        var bytes = new List<byte>();
        for (var i = 0; i < 8192; i++) { var b = Read(address + (ulong)i, 1)[0]; if (b == 0) return Encoding.UTF8.GetString(bytes.ToArray()); bytes.Add(b); }
        throw new InvalidDataException("游戏运行时类型名称过长。");
    }
    public void Write(ulong address, byte[] bytes)
    {
        CheckAlive();
        if (!WriteProcessMemory(_handle, (IntPtr)(long)address, bytes, (nuint)bytes.Length, out var written) || written != (nuint)bytes.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法写入游戏临时缓冲区。");
    }

    public Allocation Allocate(int size)
    {
        CheckAlive();
        var address = VirtualAllocEx(_handle, IntPtr.Zero, (nuint)size, 0x3000, 0x40);
        if (address == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new Allocation(this, _handle, unchecked((ulong)address.ToInt64()));
    }
    public Allocation Utf8(string text) { var bytes = Encoding.UTF8.GetBytes(text + '\0'); var allocation = Allocate(bytes.Length); Write(allocation.Address, bytes); return allocation; }

    // Only runtime metadata and previously read-only native getters are queried here.
    public ulong Call(ulong target, ulong arg1 = 0, ulong arg2 = 0, ulong arg3 = 0, ulong arg4 = 0)
    {
        if (_uncertain) throw new InvalidOperationException("上一项运行时查询尚未确认结束，本次不重复发送。");
        using var result = Allocate(8);
        using var code = Allocate(256);
        var bytes = new List<byte>();
        void Emit(params byte[] b) => bytes.AddRange(b);
        void Mov(byte register, ulong value) { Emit(0x48, register); Emit(BitConverter.GetBytes(value)); }
        void Invoke(ulong address) { Mov(0xB8, address); Emit(0xFF, 0xD0); }
        Emit(0x53, 0x48, 0x83, 0xEC, 0x30);
        Invoke(Export("il2cpp_domain_get")); Emit(0x48, 0x89, 0xC1);
        Invoke(Export("il2cpp_thread_attach")); Emit(0x48, 0x89, 0xC3);
        Mov(0xB9, arg1); Mov(0xBA, arg2); Emit(0x49, 0xB8); Emit(BitConverter.GetBytes(arg3)); Emit(0x49, 0xB9); Emit(BitConverter.GetBytes(arg4));
        Emit(0x48, 0xC7, 0x44, 0x24, 0x20, 0, 0, 0, 0, 0x48, 0xC7, 0x44, 0x24, 0x28, 0, 0, 0, 0);
        Invoke(target); Mov(0xBA, result.Address); Emit(0x48, 0x89, 0x02, 0x48, 0x89, 0xD9);
        Invoke(Export("il2cpp_thread_detach")); Emit(0x33, 0xC0, 0x48, 0x83, 0xC4, 0x30, 0x5B, 0xC3);
        Write(code.Address, bytes.ToArray());
        var thread = CreateRemoteThread(_handle, IntPtr.Zero, 0, (IntPtr)(long)code.Address, IntPtr.Zero, 0, out _);
        if (thread == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (WaitForSingleObject(thread, 5000) != 0)
            {
                _uncertain = true;
                code.Retain(); result.Retain();
                throw new TimeoutException("游戏运行时查询未在五秒内完成；本次没有执行数值修改。");
            }
            return ReadPointer(result.Address);
        }
        finally { CloseHandle(thread); }
    }

    public void Dispose() { CloseHandle(_handle); _process.Dispose(); }
    private static bool SamePath(string? a, string b) => a != null && Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    internal readonly record struct NativeMethod(ulong Pointer, ulong Info);
    internal sealed class Allocation(Il2CppRuntimeResolver owner, IntPtr handle, ulong address) : IDisposable
    {
        public ulong Address { get; } = address;
        private bool _retain;
        public void Retain() => _retain = true;
        public void Dispose() { if (!_retain && !owner._uncertain) VirtualFreeEx(handle, (IntPtr)(long)Address, 0, 0x8000); }
    }

    private static Dictionary<string, ulong> ReadExports(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64) throw new InvalidOperationException("本模块需要 64 位 IL2CPP 游戏。");
        var directory = pe.PEHeaders.PEHeader!.ExportTableDirectory;
        if (directory.RelativeVirtualAddress == 0) throw new InvalidDataException("GameAssembly 没有运行时导出。");
        byte[] At(int rva, int length) => pe.GetSectionData(rva).GetContent(0, length).ToArray();
        var header = At(directory.RelativeVirtualAddress, 40);
        var count = BitConverter.ToInt32(header, 24);
        if (count is < 1 or > 65536) throw new InvalidDataException("GameAssembly 导出表无效。");
        var names = At(BitConverter.ToInt32(header, 32), count * 4);
        var ordinals = At(BitConverter.ToInt32(header, 36), count * 2);
        var result = new Dictionary<string, ulong>();
        for (var i = 0; i < count; i++)
        {
            var nameBlock = pe.GetSectionData(BitConverter.ToInt32(names, i * 4));
            var nameBytes = nameBlock.GetContent(0, Math.Min(nameBlock.Length, 8192)).ToArray();
            var end = Array.IndexOf(nameBytes, (byte)0);
            if (end is < 0 or > 8192) throw new InvalidDataException("导出名称无效。");
            var ordinal = BitConverter.ToUInt16(ordinals, i * 2);
            var rva = BitConverter.ToUInt32(At(BitConverter.ToInt32(header, 28) + ordinal * 4, 4));
            if (rva >= directory.RelativeVirtualAddress && rva < directory.RelativeVirtualAddress + directory.Size)
                continue; // Forwarded exports are not callable code addresses.
            result[Encoding.ASCII.GetString(nameBytes, 0, end)] = rva;
        }
        return result;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] bytes, nuint count, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] bytes, nuint count, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint type, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, nuint stackSize, IntPtr address, IntPtr argument, uint flags, out uint id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
