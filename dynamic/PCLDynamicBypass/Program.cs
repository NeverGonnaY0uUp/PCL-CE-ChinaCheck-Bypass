using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;

namespace PCLDynamicBypass;

/// <summary>
///     双击运行：对同目录的 Plain Craft Launcher 2.exe 做"临时动态补丁"，
///     把 RegionAssessment.get_IsRestricted 的 IL 改成 return false，然后拉起启动器；
///     启动器退出后自动还原原文件。若启动器异常退出/本进程被强杀，下次运行会自动修复。
///
///     注意：这是"运行时自动补丁 + 自动还原"，不是纯内存补丁：
///     PCL 是 .NET 单文件 bundle，内嵌程序集以只读文件映射加载，内存页不可写，
///     因此只能在启动前改文件字节、退出后写回。原文件在每次运行结束后保持不变。
/// </summary>
internal static class Program
{
    private const string LauncherExeName = "Plain Craft Launcher 2.exe";
    private const string CoreAssemblyName = "PCL.Core.dll";
    private const string BakSuffix = ".pclbypass.bak";
    private const string MarkerSuffix = ".pclbypass.marker";

    private static string _logPath = "";
    private static string _markerPath = "";
    private static string _bakPath = "";
    private static string _targetPath = "";

    [STAThread]
    private static int Main(string[] args)
    {
        _logPath = Path.Combine(Path.GetTempPath(), "PCLDynamicBypass", "launcher.log");
        try
        {
            Log("=== launcher start ===");
            var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

            var passthrough = new List<string>();
            string? explicitTarget = null;
            for (var i = 0; i < args.Length; i++)
                if (args[i] is "--target" && i + 1 < args.Length)
                    explicitTarget = args[++i];
                else
                    passthrough.Add(args[i]);

            var target = FindTarget(baseDir, explicitTarget, out var findError);
            if (target is null)
            {
                Fail(findError ?? $"同目录下没有找到可用的 PCL 启动器 exe。\n\n目录：{baseDir}\n\n" +
                     "要求：目录中恰好有一个文件名包含 \"PCL\" 的 exe（本程序自身除外）。");
                return 1;
            }

            _bakPath = target + BakSuffix;
            _markerPath = target + MarkerSuffix;
            _targetPath = target;
            Log($"target={target}");

            // 1) 先处理上次异常退出遗留的补丁。
            RecoverIfNeeded(target);

            // 2) 读当前状态，判断是否已经被另一个实例打过补丁。
            var originalBytes = File.ReadAllBytes(target);
            var originalHash = Sha256(originalBytes);
            var marker = Marker.Read(_markerPath);
            if (marker is not null && string.Equals(marker.PatchedHash, originalHash, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(_bakPath))
            {
                Log("检测到已有实例处于补丁状态，直接拉起启动器（由原实例负责还原）");
                Launch(target, passthrough);
                return 0;
            }

            // 3) 在内存里生成补丁字节，并计算补丁后的哈希。
            byte[] patchedBytes;
            try
            {
                patchedBytes = PatchBytes(originalBytes);
            }
            catch (Exception ex)
            {
                Log("生成补丁失败: " + ex);
                Fail("无法为当前构建生成补丁：\n" + ex.Message +
                     "\n\n请确认这是 PCL-CE 2.15.x 的单文件构建。");
                return 1;
            }

            var patchedHash = Sha256(patchedBytes);
            Log($"originalHash={originalHash}");
            Log($"patchedHash={patchedHash}");

            // 4) 先写备份与标记，再原子替换 exe，保证任何时刻崩溃都能恢复。
            File.WriteAllBytes(_bakPath, originalBytes);
            Marker.Write(_markerPath, originalHash, patchedHash);
            WriteFileAtomically(target, patchedBytes);
            Log("已临时写入补丁，启动启动器...");

            // 5) 启动，并等待 PCL 全部退出（含它自己重启出的新进程）。
            var process = Launch(target, passthrough);
            if (process is null)
            {
                Restore(target, patchedHash);
                Fail("启动 Plain Craft Launcher 2.exe 失败。");
                return 1;
            }

            WaitForAllInstancesExit(target, process);

            // 6) 还原原文件。
            Restore(target, patchedHash);
            Log("已还原原始 exe");
            return 0;
        }
        catch (Exception ex)
        {
            Log("launcher fatal: " + ex);
            // 尽量把文件恢复到原始状态，避免留下半补丁。
            try
            {
                if (File.Exists(_markerPath) && File.Exists(_bakPath) && File.Exists(_targetPath))
                {
                    var marker = Marker.Read(_markerPath);
                    if (marker is not null)
                    {
                        var currentHash = Sha256(File.ReadAllBytes(_targetPath));
                        if (string.Equals(currentHash, marker.PatchedHash, StringComparison.OrdinalIgnoreCase))
                            WriteFileAtomically(_targetPath, File.ReadAllBytes(_bakPath));
                    }

                    Cleanup();
                }
            }
            catch
            {
            }

            Fail("运行失败：" + ex.Message);
            return 1;
        }
    }

    // ---------------------------------------------------------------- patch generation

    private static byte[] PatchBytes(byte[] exeBytes)
    {
        var bundle = TryReadBundle(exeBytes);
        var entry = bundle.Entries.FirstOrDefault(e =>
                        string.Equals(Path.GetFileName(e.Name.Replace('\\', '/')), CoreAssemblyName,
                            StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("单文件 bundle 中找不到 PCL.Core.dll");
        if (entry.CompressedSize != 0)
            throw new InvalidOperationException("PCL.Core.dll 在 bundle 中是压缩存储的，暂不支持");
        if (entry.Size < 64 || entry.Offset < 0 || entry.Offset + entry.Size > exeBytes.Length)
            throw new InvalidOperationException("bundle 条目越界");

        var patched = (byte[])exeBytes.Clone();
        var core = patched.AsSpan((int)entry.Offset, (int)entry.Size).ToArray();
        PatchCore(core);
        core.CopyTo(patched.AsSpan((int)entry.Offset));
        return patched;
    }

    private static void PatchCore(byte[] dll)
    {
        using var stream = new MemoryStream(dll, false);
        using var pe = new PEReader(stream, PEStreamOptions.PrefetchMetadata);
        var reader = pe.GetMetadataReader();

        MethodDefinitionHandle? found = null;
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            if (reader.GetString(type.Namespace) != "PCL.Core.App.Region" ||
                reader.GetString(type.Name) != "RegionAssessment") continue;
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (reader.GetString(method.Name) != "get_IsRestricted") continue;
                found = methodHandle;
                break;
            }

            if (found is not null) break;
        }

        if (found is null) throw new InvalidOperationException("找不到 RegionAssessment.get_IsRestricted");
        var definition = reader.GetMethodDefinition(found.Value);
        var rva = definition.RelativeVirtualAddress;
        if (rva == 0) throw new InvalidOperationException("目标方法没有 IL 方法体");

        var bodyOffset = RvaToFileOffset(pe.PEHeaders, rva);
        var first = dll[bodyOffset];
        int headerSize;
        int codeSize;
        bool fat;
        if ((first & 3) == 2)
        {
            fat = false;
            headerSize = 1;
            codeSize = first >> 2;
        }
        else if ((first & 3) == 3)
        {
            fat = true;
            var flagsSize = BitConverter.ToUInt16(dll, bodyOffset);
            headerSize = ((flagsSize >> 12) & 0xF) * 4;
            codeSize = BitConverter.ToInt32(dll, bodyOffset + 4);
        }
        else
        {
            throw new InvalidOperationException("目标方法不是合法的 IL 方法体");
        }

        if (codeSize < 2 || codeSize > 4096) throw new InvalidOperationException($"方法体长度异常：{codeSize}");
        var codeOffset = bodyOffset + headerSize;
        if (dll[codeOffset] == 0x16 && dll[codeOffset + 1] == 0x2A && codeSize == 2) return; // 已是补丁状态

        // 等长方案会触发 .NET 10 JIT 的 InvalidProgramException，所以同时把 CodeSize 改为 2。
        if (fat)
        {
            BitConverter.GetBytes(2u).CopyTo(dll, bodyOffset + 4);
        }
        else
        {
            dll[bodyOffset] = (byte)((2 << 2) | 2);
        }

        dll[codeOffset] = 0x16; // ldc.i4.0
        dll[codeOffset + 1] = 0x2A; // ret
    }

    private static int RvaToFileOffset(PEHeaders headers, int rva)
    {
        foreach (var section in headers.SectionHeaders)
        {
            var end = section.VirtualAddress + Math.Max(section.VirtualSize, section.SizeOfRawData);
            if (rva < section.VirtualAddress || rva >= end) continue;
            var delta = rva - section.VirtualAddress;
            if (delta >= section.SizeOfRawData) break;
            return section.PointerToRawData + delta;
        }

        throw new InvalidOperationException($"无法把 RVA 0x{rva:X} 映射到文件偏移");
    }

    // ---------------------------------------------------------------- bundle parsing

    private static readonly byte[] BundleSignature =
    [
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
        0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
        0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
    ];

    private sealed class BundleEntry
    {
        public required string Name { get; init; }
        public required long Offset { get; init; }
        public required long Size { get; init; }
        public required long CompressedSize { get; init; }
    }

    private sealed class BundleInfo
    {
        public required List<BundleEntry> Entries { get; init; }
    }

    private static BundleInfo TryReadBundle(byte[] bytes)
    {
        var searchStart = bytes.Length;
        var headerOffset = -1L;
        while (searchStart > 16)
        {
            var index = bytes.AsSpan(0, searchStart).LastIndexOf(BundleSignature);
            if (index < 8) break;
            var candidate = BitConverter.ToInt64(bytes, index - 8);
            if (candidate > index + BundleSignature.Length && candidate < bytes.Length - 16)
            {
                headerOffset = candidate;
                break;
            }

            searchStart = index + BundleSignature.Length - 1;
        }

        if (headerOffset < 0)
            throw new InvalidOperationException("目标 exe 不是 .NET 单文件 bundle（可能是目录构建）");

        var position = checked((int)headerOffset);
        var major = BitConverter.ToUInt32(bytes, position);
        position += 4;
        position += 4; // minor
        var count = BitConverter.ToUInt32(bytes, position);
        position += 4;
        position += 1 + bytes[position]; // BundleID（7-bit length 编码，长度通常 < 128）

        if (major >= 2) position += 8 * 4 + 8; // deps / runtimeconfig / flags

        var entries = new List<BundleEntry>((int)count);
        for (var i = 0; i < count; i++)
        {
            var offset = BitConverter.ToInt64(bytes, position);
            position += 8;
            var size = BitConverter.ToInt64(bytes, position);
            position += 8;
            var compressed = major >= 6 ? BitConverter.ToInt64(bytes, position) : 0;
            if (major >= 6) position += 8;
            position += 1; // type
            var nameLength = bytes[position++];
            var name = Encoding.UTF8.GetString(bytes, position, nameLength);
            position += nameLength;
            entries.Add(new BundleEntry { Name = name, Offset = offset, Size = size, CompressedSize = compressed });
        }

        return new BundleInfo { Entries = entries };
    }

    // ---------------------------------------------------------------- restore / wait

    private static void RecoverIfNeeded(string target)
    {
        if (!File.Exists(_markerPath) || !File.Exists(_bakPath)) return;
        var marker = Marker.Read(_markerPath);
        if (marker is null)
        {
            Cleanup();
            return;
        }

        var currentHash = Sha256(File.ReadAllBytes(target));
        if (string.Equals(currentHash, marker.PatchedHash, StringComparison.OrdinalIgnoreCase))
        {
            Log("发现上次异常退出遗留的补丁，正在还原...");
            WriteFileAtomically(target, File.ReadAllBytes(_bakPath));
            Log("已从备份还原");
        }
        else
        {
            Log("上次的备份与当前 exe 不一致（可能已更新），仅清理备份");
        }

        Cleanup();
    }

    private static void Restore(string target, string patchedHash)
    {
        try
        {
            var currentHash = Sha256(File.ReadAllBytes(target));
            if (string.Equals(currentHash, patchedHash, StringComparison.OrdinalIgnoreCase) && File.Exists(_bakPath))
            {
                WriteFileAtomically(target, File.ReadAllBytes(_bakPath));
            }
            else
            {
                Log("当前 exe 已不是补丁状态（可能被更新），跳过还原");
            }
        }
        catch (Exception ex)
        {
            Log("还原失败: " + ex.Message);
            return;
        }

        Cleanup();
    }

    private static void Cleanup()
    {
        try
        {
            File.Delete(_markerPath);
        }
        catch
        {
        }

        try
        {
            File.Delete(_bakPath);
        }
        catch
        {
        }
    }

    private static void WaitForAllInstancesExit(string target, Process first)
    {
        try
        {
            first.WaitForExit();
        }
        catch
        {
        }

        var name = Path.GetFileNameWithoutExtension(target);
        var idleSince = DateTime.UtcNow;
        for (var i = 0; i < 24 * 3600; i++)
        {
            var running = Process.GetProcessesByName(name).Any(p =>
            {
                try
                {
                    return string.Equals(p.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
                finally
                {
                    p.Dispose();
                }
            });

            if (running)
            {
                idleSince = DateTime.UtcNow;
            }
            else if ((DateTime.UtcNow - idleSince).TotalMilliseconds > 1500)
            {
                return;
            }

            Thread.Sleep(500);
        }
    }

    private static Process? Launch(string target, List<string> passthrough)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = target,
            WorkingDirectory = Path.GetDirectoryName(target)!,
            UseShellExecute = false
        };
        foreach (var arg in passthrough) startInfo.ArgumentList.Add(arg);
        try
        {
            return Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Log("启动失败: " + ex.Message);
            return null;
        }
    }

    private static void WriteFileAtomically(string path, byte[] bytes)
    {
        var temp = path + $".pclbypass.tmp-{Environment.ProcessId}";
        File.WriteAllBytes(temp, bytes);

        // 现实环境里 exe 可能被杀软/索引器短暂占用，Move/Copy 会偶发 Access denied，
        // 这里按“原子替换 -> 覆盖复制 -> 直接覆盖写”三级降级并做重试。
        var errors = new List<string>();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                File.Move(temp, path, true);
                return;
            }
            catch (Exception ex)
            {
                errors.Add("move:" + ex.GetType().Name);
            }

            Thread.Sleep(150);
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                File.Copy(temp, path, true);
                TryDelete(temp);
                return;
            }
            catch (Exception ex)
            {
                errors.Add("copy:" + ex.GetType().Name);
            }

            Thread.Sleep(150);
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                File.WriteAllBytes(path, bytes);
                TryDelete(temp);
                return;
            }
            catch (Exception ex)
            {
                errors.Add("write:" + ex.GetType().Name);
            }

            Thread.Sleep(150);
        }

        TryDelete(temp);
        throw new IOException("无法写入目标 exe（可能被杀软/权限拦截）：" + string.Join(", ", errors.Distinct()));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    // ---------------------------------------------------------------- helpers / ui

    private sealed class Marker
    {
        public required string OriginalHash { get; init; }
        public required string PatchedHash { get; init; }

        public static Marker? Read(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var lines = File.ReadAllLines(path);
                var original = lines.FirstOrDefault(l => l.StartsWith("original=", StringComparison.Ordinal))?[9..];
                var patched = lines.FirstOrDefault(l => l.StartsWith("patched=", StringComparison.Ordinal))?[8..];
                if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(patched)) return null;
                return new Marker { OriginalHash = original, PatchedHash = patched };
            }
            catch
            {
                return null;
            }
        }

        public static void Write(string path, string originalHash, string patchedHash)
        {
            File.WriteAllText(path, $"original={originalHash}\npatched={patchedHash}\n");
        }
    }

    private static string? FindTarget(string baseDir, string? explicitTarget, out string? error)
    {
        error = null;
        if (!string.IsNullOrWhiteSpace(explicitTarget))
        {
            var path = Path.GetFullPath(explicitTarget);
            if (Directory.Exists(path)) path = Path.Combine(path, LauncherExeName);
            if (File.Exists(path)) return path;
            error = "指定的目标不存在：" + explicitTarget;
            return null;
        }

        // 规则：同目录中“文件名包含 PCL”的 exe（不区分大小写），排除本程序自身。
        var allExe = Directory.GetFiles(baseDir, "*.exe")
            .Where(path => !IsSelf(path))
            .ToList();

        var candidates = allExe
            .Where(path => Path.GetFileName(path).Contains("PCL", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            // 兼容原版命名：官方产物叫 "Plain Craft Launcher 2.exe"，不含 "PCL" 字样。
            var stock = allExe
                .Where(path => Path.GetFileName(path).StartsWith("Plain Craft Launcher",
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (stock.Count == 1)
            {
                Log("未找到文件名包含 PCL 的 exe，回退到官方命名: " + Path.GetFileName(stock[0]));
                candidates = stock;
            }
        }

        if (candidates.Count == 0) return null;

        if (candidates.Count > 1)
        {
            Log("找到多个 PCL exe，终止启动：\n  " + string.Join("\n  ", candidates.Select(Path.GetFileName)));
            error = "目录中找到多个文件名包含 \"PCL\" 的 exe，已终止启动：\n\n" +
                    string.Join("\n", candidates.Select(path => "  • " + Path.GetFileName(path))) +
                    "\n\n请只保留一个 PCL 启动器后再运行。";
            return null;
        }

        return candidates[0];
    }

    private static bool IsSelf(string path)
    {
        var full = Path.GetFullPath(path);

        var self = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(self) &&
            string.Equals(full, Path.GetFullPath(self), StringComparison.OrdinalIgnoreCase))
            return true;

        // 兜底：按本程序的文件名排除（防止 ProcessPath 为空或被重定向）。
        var selfName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        return !string.IsNullOrEmpty(selfName) &&
               string.Equals(Path.GetFileNameWithoutExtension(full), selfName,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    private static void Fail(string message)
    {
        Log("FAIL: " + message);
        if (string.Equals(Environment.GetEnvironmentVariable("PCLBYPASS_NO_UI"), "1", StringComparison.Ordinal))
            return;
        try
        {
            MessageBoxW(IntPtr.Zero, message, "PCL Dynamic Region Bypass", 0x10);
        }
        catch
        {
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath,
                $"[{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
