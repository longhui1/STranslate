using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PaddleOcrSmoke;

internal sealed class NativeRuntime
{
    private readonly List<string> _crtPaths = [];
    // 测试进程结束时由 Windows 释放这些句柄，避免异步插件清理期间卸载 DLL。
    private readonly List<IntPtr> _directoryCookies = [];
    private readonly List<IntPtr> _preloadedLibraries = [];

    internal NativeRuntime(string appDirectory, string pluginDirectory)
    {
        if (!SetDefaultDllDirectories(0x1000))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var directories = new[] { appDirectory, pluginDirectory }
            .Concat(Directory.EnumerateFiles(pluginDirectory, "*.dll", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName).OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            var cookie = AddDllDirectory(Path.GetFullPath(directory));
            if (cookie == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _directoryCookies.Add(cookie);
        }
        var crtPaths = Directory.EnumerateFiles(appDirectory, "*.dll")
            .Where(path => new[] { "vcruntime140", "msvcp140", "vccorlib140", "concrt140", "vcomp140" }
                .Any(prefix => Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(path => Path.GetFileName(path).Equals("vcruntime140.dll", StringComparison.OrdinalIgnoreCase) ? 0 :
                Path.GetFileName(path).StartsWith("vcruntime", StringComparison.OrdinalIgnoreCase) ? 1 :
                Path.GetFileName(path).Equals("msvcp140.dll", StringComparison.OrdinalIgnoreCase) ? 2 : 3).ToArray();
        foreach (var required in new[] { "vcruntime140.dll", "msvcp140.dll", "msvcp140_1.dll" })
            if (!crtPaths.Any(path => Path.GetFileName(path).Equals(required, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"发布包缺少 ARM64 C++ Runtime：{required}。");
        foreach (var path in crtPaths)
        {
            RequireArm64(path);
            _preloadedLibraries.Add(NativeLibrary.Load(path));
            _crtPaths.Add(path);
        }
    }

    internal List<NativeModuleObservation> VerifyLoaded(string pluginDirectory)
    {
        using var process = Process.GetCurrentProcess();
        var modules = process.Modules.Cast<ProcessModule>().ToArray();
        var expectedPaths = new[] { "onnxruntime.dll", "libSkiaSharp.dll" }
            .Select(name => Directory.EnumerateFiles(pluginDirectory, name, SearchOption.AllDirectories).Single())
            .Concat(_crtPaths).ToArray();
        var observations = new List<NativeModuleObservation>();
        foreach (var path in expectedPaths)
        {
            var matching = modules.Where(module => string.Equals(Path.GetFileName(module.FileName),
                Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matching.Length != 1 || !string.Equals(Path.GetFullPath(matching[0].FileName),
                    Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"实际进程未从发布包加载原生模块：{path}");
            var loaded = matching[0];
            RequireArm64(loaded.FileName);
            observations.Add(new(Path.GetFileName(path), loaded.FileName, "0xAA64",
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
        }
        return observations;
    }

    internal static void RequireArm64(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt16() != 0x5a4d) throw new InvalidDataException($"不是 PE 文件：{path}");
        stream.Position = 0x3c;
        var peOffset = reader.ReadInt32();
        stream.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0xaa64)
            throw new InvalidDataException($"原生模块不是 Windows ARM64：{path}");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);
}

internal sealed record NativeModuleObservation(string Name, string LoadedPath, string Machine, string SHA256);
