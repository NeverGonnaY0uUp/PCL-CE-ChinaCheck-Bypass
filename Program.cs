using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace PCLRegionBypass;

/// <summary>
///     PCL-CE 中国大陆区域限制绕过工具。
///     目标：把 PCL.Core.dll 里 RegionAssessment.get_IsRestricted 的 IL 换成 "return false"，
///     从而让 RestrictedFeaturePolicy 的所有区域开关放行。
///     支持两种形态：目录构建（磁盘上的 PCL.Core.dll）与已发布单文件 exe（bundle 内嵌 PCL.Core.dll）。
/// </summary>
internal static class Program
{
    private const string CoreFileName = "PCL.Core.dll";
    private const string TargetNamespace = "PCL.Core.App.Region";
    private const string TargetTypeName = "RegionAssessment";
    private const string TargetMethodName = "get_IsRestricted";

    // apphost 中的 bundle 签名：.NET 单文件 bundle 的 32 字节 SHA-256 标记，紧邻其前的 8 字节是 header 偏移。
    private static readonly byte[] BundleSignature =
    [
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
        0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
        0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
    ];

    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 某些非交互环境不允许改编码，忽略。
        }

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        try
        {
            return command switch
            {
                "inspect" => Inspect(rest),
                "patch" => Patch(rest),
                "extract" => Extract(rest),
                "verify" => Verify(rest),
                "diagnose" => Diagnose(rest),
                _ => Unknown(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"[FAIL] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"[FAIL] 未知命令: {command}（可用: inspect / patch / extract / verify）");
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            PCLRegionBypass - PCL-CE 中国大陆区域限制绕过工具（只针对已构建产物，不改源码）

            用法:
              PCLRegionBypass inspect <exe|dll|目录>
                  只读分析：识别单文件 bundle / 定位 PCL.Core.dll / 打印目标方法 IL，不修改任何文件。

              PCLRegionBypass patch <exe|dll|目录> [--no-backup]
                  把 RegionAssessment.get_IsRestricted 的 IL 改写为 "return false"（等长 NOP 填充）。
                  exe 为单文件 bundle 时直接改 exe 内嵌的 PCL.Core.dll；目录构建时改磁盘上的 PCL.Core.dll。
                  默认在目标旁生成 .pclregionbak 备份。

              PCLRegionBypass extract <exe> <entryName> <输出路径>
                  从单文件 bundle 中导出指定条目（用于比对/验证），如 PCL.Core.dll。

              PCLRegionBypass verify <PCL.Core.dll 路径>
                  反射加载该 DLL，对 Unknown/Other/Mainland 三种分类分别打印 IsRestricted。
                  原版：Mainland=True、Unknown=True；补丁后：全部 False。

              PCLRegionBypass diagnose <PCL.Core.dll 路径>
                  用该 DLL 真实调用 RegionPolicy.ReadWindowsSignals/Evaluate，打印本机
                  采集到的区域信号与最终判定（用于确认本机当前是否会被拦截）。

            示例:
              PCLRegionBypass inspect "C:\\PCL\\Plain Craft Launcher 2.exe"
              PCLRegionBypass patch   "C:\\PCL\\Plain Craft Launcher 2.exe"
              PCLRegionBypass verify  "C:\\PCL\\PCL.Core.dll"
            """);
    }

    // ------------------------------------------------------------------ commands

    private static int Inspect(string[] args)
    {
        var target = LoadTarget(RequirePath(args, 0));
        PrintTarget(target);

        var method = FindTargetMethod(target.CoreBytes);
        Console.WriteLine();
        Console.WriteLine($"[目标方法] {TargetNamespace}.{TargetTypeName}::{TargetMethodName}");
        Console.WriteLine($"  方法体文件偏移: {method.BodyOffsetInCore}（DLL 内），代码偏移: {method.CodeOffsetInCore}，长度: {method.CodeSize} 字节");
        Console.WriteLine($"  原始 IL: {Hex(target.CoreBytes.AsSpan(method.CodeOffsetInCore, method.CodeSize))}");
        Console.WriteLine($"  判定: {(method.IsPatched ? "已被本工具patch过" : "尚未修改")}");
        return 0;
    }

    private static int Patch(string[] args)
    {
        var input = RequirePath(args, 0);
        var noBackup = args.Any(a => string.Equals(a, "--no-backup", StringComparison.OrdinalIgnoreCase));
        var target = LoadTarget(input);
        PrintTarget(target);

        var method = FindTargetMethod(target.CoreBytes);
        var original = target.CoreBytes.AsSpan(method.CodeOffsetInCore, method.CodeSize).ToArray();
        Console.WriteLine();
        Console.WriteLine($"[目标方法] {TargetNamespace}.{TargetTypeName}::{TargetMethodName}，代码长度 {method.CodeSize} 字节，方法头: {(method.HasFatHeader ? "fat" : "tiny")}");
        Console.WriteLine($"  原始 IL: {Hex(original)}");

        if (method.IsPatched)
        {
            Console.WriteLine();
            Console.WriteLine("[SKIP] 目标方法已经是 \"return false\"，无需重复 patch。");
            return 0;
        }

        if (method.CodeSize < 2) throw new InvalidOperationException($"方法体过小（{method.CodeSize} 字节），不是预期的 getter。");

        // 新 IL: ldc.i4.0; ret（2 字节，不引用任何 metadata token，因此无需重写元数据）。
        // 注意：实测 .NET 10 JIT 会拒绝 "ret 后面补 NOP 填满原长度" 的写法（InvalidProgramException），
        // 所以这里同时把方法头里的 CodeSize 改成 2；文件布局不变（后续方法 RVA 由元数据固定，不受影响）。
        var patched = new byte[] { 0x16, 0x2A };
        var codeOffsetInFile = target.CoreFileBase + method.CodeOffsetInCore;
        patched.CopyTo(target.FileBytes.AsSpan(codeOffsetInFile));

        if (method.HasFatHeader)
        {
            BitConverter.GetBytes((uint)patched.Length)
                .CopyTo(target.FileBytes.AsSpan(target.CoreFileBase + method.BodyOffsetInCore + 4));
            Console.WriteLine($"  方法头 CodeSize: {method.CodeSize} -> {patched.Length}（fat header @ {method.BodyOffsetInCore}）");
        }
        else
        {
            // tiny header: 低 2 位为 2，高 6 位为代码长度；长度 2 -> 0b000010_10 = 0x0A
            target.FileBytes[target.CoreFileBase + method.BodyOffsetInCore] = (byte)((patched.Length << 2) | 2);
            Console.WriteLine($"  方法头 CodeSize: {method.CodeSize} -> {patched.Length}（tiny header @ {method.BodyOffsetInCore}）");
        }

        Console.WriteLine($"  补丁 IL: {Hex(patched)}（代码偏移 {method.CodeOffsetInCore}）");
        Console.WriteLine($"  写入位置: {target.InputPath} 偏移 {codeOffsetInFile}");

        if (target.IsBundle)
            Console.WriteLine($"  说明: 修改的是 exe 内嵌 bundle 的 PCL.Core.dll（bundle 内偏移 {target.CoreEntry!.Offset}）");
        else
            Console.WriteLine("  说明: 修改的是磁盘上的 PCL.Core.dll");

        if (!noBackup)
        {
            var backup = target.InputPath + ".pclregionbak";
            if (!File.Exists(backup))
            {
                File.Copy(target.InputPath, backup, false);
                Console.WriteLine($"  备份: {backup}");
            }
            else
            {
                Console.WriteLine($"  备份已存在，未覆盖: {backup}");
            }
        }

        // 先写临时文件再原子替换，避免写一半损坏产物。
        var temp = target.InputPath + $".pclregiontmp-{Environment.ProcessId}";
        File.WriteAllBytes(temp, target.FileBytes);
        File.Move(temp, target.InputPath, true);

        Console.WriteLine();
        Console.WriteLine("[OK] 补丁已写入。");
        Console.WriteLine("  启动补丁后的启动器，中国大陆区域限制（安装/下载/补全/整合包）将不再拦截。");
        Console.WriteLine("  注意: 启动器自更新会用官方文件覆盖 exe，需要重新 patch。");
        return 0;
    }

    private static int Extract(string[] args)
    {
        var input = RequirePath(args, 0);
        if (args.Length < 3) throw new ArgumentException("用法: extract <exe> <entryName> <输出路径>");
        var entryName = args[1];
        var output = Path.GetFullPath(args[2]);

        var bytes = File.ReadAllBytes(Path.GetFullPath(input));
        if (!TryFindBundle(bytes, out var headerOffset))
            throw new InvalidOperationException("输入文件不是 .NET 单文件 bundle（未找到 bundle 签名）。");

        var bundle = ReadBundle(bytes, headerOffset);
        var entry = bundle.Entries.FirstOrDefault(e =>
                        string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetFileName(e.Name.Replace('\\', '/')), entryName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"bundle 中找不到条目: {entryName}");

        if (entry.CompressedSize != 0)
            throw new InvalidOperationException("该条目在 bundle 中是压缩存储的，暂不支持导出。");

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllBytes(output, bytes.AsSpan((int)entry.Offset, (int)entry.Size).ToArray());
        Console.WriteLine($"[OK] 已导出 {entry.Name} ({entry.Size} 字节) -> {output}");
        return 0;
    }

    private static int Verify(string[] args)
    {
        var path = Path.GetFullPath(RequirePath(args, 0));
        var assembly = Assembly.LoadFrom(path);
        var assessmentType = assembly.GetType($"{TargetNamespace}.{TargetTypeName}", true)!;
        var classificationType = assembly.GetType($"{TargetNamespace}.RegionClassification", true)!;
        var ctor = assessmentType.GetConstructor([classificationType, typeof(string)])
                   ?? throw new InvalidOperationException("找不到 RegionAssessment(RegionClassification, string) 构造函数。");
        var property = assessmentType.GetProperty("IsRestricted", BindingFlags.Public | BindingFlags.Instance)
                       ?? throw new InvalidOperationException("找不到 RegionAssessment.IsRestricted 属性。");

        Console.WriteLine($"[程序集] {assembly.FullName}");
        Console.WriteLine($"[属性]   {assessmentType.FullName}.IsRestricted");
        Console.WriteLine();
        var anyWrong = false;
        foreach (var value in Enum.GetValues(classificationType))
        {
            try
            {
                var instance = ctor.Invoke([value, "verify-tool"]);
                var restricted = (bool)property.GetValue(instance)!;
                var expected = false; // 补丁后的预期：任何分类都不受限
                if (restricted != expected) anyWrong = true;
                Console.WriteLine($"  Classification={value,-8} IsRestricted={restricted}");
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
                Console.WriteLine($"  Classification={value,-8} 调用失败: {inner.GetType().Name}: {inner.Message}");
                Console.WriteLine($"    {inner.StackTrace}");
                anyWrong = true;
            }
        }

        Console.WriteLine();
        if (anyWrong)
        {
            Console.WriteLine("[RESULT] 仍有分类返回 IsRestricted=True —— 该 DLL 未打补丁（或补丁未生效）。");
            return 1;
        }

        Console.WriteLine("[RESULT] 所有分类 IsRestricted=False —— 补丁生效。");
        return 0;
    }

    /// <summary>
    ///     用目标 PCL.Core.dll 真实跑一遍 RegionPolicy 判定，用于确认"在本机当前区域设置下会被判成什么"。
    /// </summary>
    private static int Diagnose(string[] args)
    {
        var path = Path.GetFullPath(RequirePath(args, 0));
        var assembly = Assembly.LoadFrom(path);
        var policyType = assembly.GetType("PCL.Core.App.Region.RegionPolicy", true)!;

        var signals = policyType.GetMethod("ReadWindowsSignals", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null)!;
        var assessment = policyType.GetMethod("Evaluate", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [signals])!;
        var assessmentType = assessment.GetType();

        Console.WriteLine($"[程序集] {assembly.FullName}");
        Console.WriteLine();
        Console.WriteLine("[采集到的本机信号]");
        foreach (var property in signals.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var value = property.GetValue(signals);
            if (value is System.Collections.IEnumerable enumerable && value is not string)
                value = string.Join(", ", enumerable.Cast<object>());
            Console.WriteLine($"  {property.Name} = {value ?? "<null>"}");
        }

        Console.WriteLine();
        Console.WriteLine("[判定结果]");
        Console.WriteLine($"  Classification = {assessmentType.GetProperty("Classification")!.GetValue(assessment)}");
        Console.WriteLine($"  Reason         = {assessmentType.GetProperty("Reason")!.GetValue(assessment)}");
        Console.WriteLine($"  IsRestricted   = {assessmentType.GetProperty("IsRestricted")!.GetValue(assessment)}");
        return 0;
    }

    // ------------------------------------------------------------------ target loading

    private sealed class Target
    {
        public required string InputPath { get; init; }
        public required byte[] FileBytes { get; init; }
        public required byte[] CoreBytes { get; init; }
        public required int CoreFileBase { get; init; }
        public bool IsBundle { get; init; }
        public BundleEntry? CoreEntry { get; init; }
        public BundleInfo? Bundle { get; init; }
        public string Description { get; init; } = "";
    }

    private static Target LoadTarget(string path)
    {
        var full = Path.GetFullPath(path);

        // 1) 传目录：找目录下的 PCL.Core.dll（或者唯一 exe）
        if (Directory.Exists(full))
        {
            var corePath = Path.Combine(full, CoreFileName);
            if (File.Exists(corePath))
            {
                var bytes = File.ReadAllBytes(corePath);
                return new Target
                {
                    InputPath = corePath, FileBytes = bytes, CoreBytes = bytes, CoreFileBase = 0,
                    Description = "目录构建（直接 patch PCL.Core.dll）"
                };
            }

            var exe = Directory.GetFiles(full, "*.exe").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault()
                      ?? throw new FileNotFoundException($"目录 {full} 中既没有 {CoreFileName}，也没有 exe。");
            full = exe;
            Console.WriteLine($"[info] 目录中没有独立 {CoreFileName}，改用其中的 exe: {exe}");
        }

        if (!File.Exists(full)) throw new FileNotFoundException("找不到目标文件", full);

        // 2) 直接传 PCL.Core.dll
        if (string.Equals(Path.GetFileName(full), CoreFileName, StringComparison.OrdinalIgnoreCase))
        {
            var bytes = File.ReadAllBytes(full);
            return new Target
            {
                InputPath = full, FileBytes = bytes, CoreBytes = bytes, CoreFileBase = 0,
                Description = "独立 PCL.Core.dll"
            };
        }

        // 3) 传 exe：优先按单文件 bundle 处理
        var exeBytes = File.ReadAllBytes(full);
        if (TryFindBundle(exeBytes, out var headerOffset))
        {
            var bundle = ReadBundle(exeBytes, headerOffset);
            var entry = bundle.Entries.FirstOrDefault(e =>
                string.Equals(Path.GetFileName(e.Name.Replace('\\', '/')), CoreFileName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"单文件 bundle 中找不到 {CoreFileName}（共 {bundle.Entries.Count} 个条目）。");

            if (entry.CompressedSize != 0)
                throw new InvalidOperationException("bundle 中的 PCL.Core.dll 是压缩存储的（EnableCompressionInSingleFile=true），暂不支持。");

            if (entry.Size < 64 || entry.Offset < 0 || entry.Offset + entry.Size > exeBytes.Length)
                throw new InvalidOperationException("bundle 条目越界，文件可能已损坏或格式不受支持。");

            if (exeBytes[entry.Offset] != (byte)'M' || exeBytes[entry.Offset + 1] != (byte)'Z')
                throw new InvalidOperationException("bundle 条目内容不是 PE 文件（不是以 MZ 开头），可能被压缩或加密。");

            var coreBytes = exeBytes.AsSpan((int)entry.Offset, (int)entry.Size).ToArray();
            return new Target
            {
                InputPath = full, FileBytes = exeBytes, CoreBytes = coreBytes,
                CoreFileBase = (int)entry.Offset, IsBundle = true, CoreEntry = entry, Bundle = bundle,
                Description = $"单文件 bundle（{bundle.MajorVersion}.{bundle.MinorVersion}，{bundle.Entries.Count} 个条目，内嵌 {CoreFileName}）"
            };
        }

        // 4) 普通 exe（非单文件）：patch 同目录的 PCL.Core.dll
        var sibling = Path.Combine(Path.GetDirectoryName(full)!, CoreFileName);
        if (File.Exists(sibling))
        {
            var bytes = File.ReadAllBytes(sibling);
            return new Target
            {
                InputPath = sibling, FileBytes = bytes, CoreBytes = bytes, CoreFileBase = 0,
                Description = $"非单文件 exe，同目录 {CoreFileName}"
            };
        }

        throw new InvalidOperationException("既不是单文件 bundle，同目录也没有 PCL.Core.dll。");
    }

    private static void PrintTarget(Target target)
    {
        Console.WriteLine($"[输入] {target.InputPath}");
        Console.WriteLine($"[形态] {target.Description}");
        if (target.Bundle is not null)
        {
            Console.WriteLine($"[bundle] 版本 {target.Bundle.MajorVersion}.{target.Bundle.MinorVersion}，BundleID={target.Bundle.BundleId}");
            Console.WriteLine($"  条目数: {target.Bundle.Entries.Count}");
            Console.WriteLine($"  {CoreFileName}: offset={target.CoreEntry!.Offset}, size={target.CoreEntry.Size}, type={target.CoreEntry.Type}");
        }
        else
        {
            Console.WriteLine($"[DLL] {target.CoreBytes.Length} 字节");
        }
    }

    // ------------------------------------------------------------------ bundle parsing

    private sealed class BundleEntry
    {
        public required string Name { get; init; }
        public required long Offset { get; init; }
        public required long Size { get; init; }
        public required long CompressedSize { get; init; }
        public required byte Type { get; init; }
    }

    private sealed class BundleInfo
    {
        public required uint MajorVersion { get; init; }
        public required uint MinorVersion { get; init; }
        public required string BundleId { get; init; }
        public required List<BundleEntry> Entries { get; init; }
    }

    private static bool TryFindBundle(byte[] bytes, out long headerOffset)
    {
        headerOffset = 0;
        var searchStart = bytes.Length;
        while (searchStart > 16)
        {
            var index = bytes.AsSpan(0, searchStart).LastIndexOf(BundleSignature);
            if (index < 8) return false;
            var candidate = BitConverter.ToInt64(bytes, index - 8);
            // 标记位于 apphost 的静态数据区（文件靠前），而 bundle header 在文件尾部，
            // 所以 header 偏移一定大于签名位置；同时必须落在文件范围内。
            if (candidate > index + BundleSignature.Length && candidate < bytes.Length - 16)
            {
                headerOffset = candidate;
                return true;
            }

            searchStart = index + BundleSignature.Length - 1;
        }

        return false;
    }

    private static BundleInfo ReadBundle(byte[] bytes, long headerOffset)
    {
        var pos = checked((int)headerOffset);
        var major = ReadUInt32(bytes, ref pos);
        var minor = ReadUInt32(bytes, ref pos);
        var numFiles = ReadUInt32(bytes, ref pos);
        var bundleId = ReadString(bytes, ref pos);
        if (major >= 2)
        {
            ReadInt64(bytes, ref pos); // deps.json offset
            ReadInt64(bytes, ref pos); // deps.json size
            ReadInt64(bytes, ref pos); // runtimeconfig.json offset
            ReadInt64(bytes, ref pos); // runtimeconfig.json size
            ReadUInt64(bytes, ref pos); // flags
        }

        if (numFiles > 100_000) throw new InvalidOperationException($"bundle 条目数异常: {numFiles}");

        var entries = new List<BundleEntry>((int)numFiles);
        for (var i = 0; i < numFiles; i++)
        {
            var offset = ReadInt64(bytes, ref pos);
            var size = ReadInt64(bytes, ref pos);
            var compressed = major >= 6 ? ReadInt64(bytes, ref pos) : 0;
            var type = bytes[pos++];
            var name = ReadString(bytes, ref pos);
            if (size < 0 || offset < 0 || offset + size > bytes.Length)
                throw new InvalidOperationException($"bundle 条目越界: {name}");
            entries.Add(new BundleEntry { Name = name, Offset = offset, Size = size, CompressedSize = compressed, Type = type });
        }

        return new BundleInfo { MajorVersion = major, MinorVersion = minor, BundleId = bundleId, Entries = entries };
    }

    private static uint ReadUInt32(byte[] bytes, ref int pos)
    {
        var value = BitConverter.ToUInt32(bytes, pos);
        pos += 4;
        return value;
    }

    private static ulong ReadUInt64(byte[] bytes, ref int pos)
    {
        var value = BitConverter.ToUInt64(bytes, pos);
        pos += 8;
        return value;
    }

    private static long ReadInt64(byte[] bytes, ref int pos)
    {
        var value = BitConverter.ToInt64(bytes, pos);
        pos += 8;
        return value;
    }

    private static string ReadString(byte[] bytes, ref int pos)
    {
        var length = 0;
        var shift = 0;
        while (true)
        {
            if (pos >= bytes.Length) throw new InvalidOperationException("bundle 头部字符串越界。");
            var b = bytes[pos++];
            length |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 28) throw new InvalidOperationException("bundle 字符串长度编码非法。");
        }

        if (length < 0 || pos + length > bytes.Length) throw new InvalidOperationException("bundle 字符串越界。");
        var value = Encoding.UTF8.GetString(bytes, pos, length);
        pos += length;
        return value;
    }

    // ------------------------------------------------------------------ dll / method

    private sealed class MethodLocation
    {
        public required int BodyOffsetInCore { get; init; }
        public required int CodeOffsetInCore { get; init; }
        public required int CodeSize { get; init; }
        public required bool HasFatHeader { get; init; }
        public required bool IsPatched { get; init; }
    }

    private static MethodLocation FindTargetMethod(byte[] dll)
    {
        using var stream = new MemoryStream(dll, false);
        using var pe = new PEReader(stream, PEStreamOptions.PrefetchMetadata);
        if (!pe.HasMetadata) throw new InvalidOperationException("PCL.Core.dll 不含 CLR 元数据。");
        var reader = pe.GetMetadataReader();

        MethodDefinitionHandle? found = null;
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (reader.GetString(type.Namespace) != TargetNamespace || reader.GetString(type.Name) != TargetTypeName) continue;
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (reader.GetString(method.Name) == TargetMethodName)
                {
                    found = methodHandle;
                    break;
                }
            }

            if (found is not null) break;
        }

        if (found is null)
            throw new InvalidOperationException($"在 {TargetNamespace}.{TargetTypeName} 中找不到方法 {TargetMethodName}。");

        var definition = reader.GetMethodDefinition(found.Value);
        var rva = definition.RelativeVirtualAddress;
        if (rva == 0) throw new InvalidOperationException("目标方法没有 IL 方法体（可能是 abstract/extern）。");

        var bodyOffset = RvaToFileOffset(pe.PEHeaders, rva);
        if (bodyOffset < 0 || bodyOffset + 1 > dll.Length) throw new InvalidOperationException("方法体偏移越界。");

        var first = dll[bodyOffset];
        int codeOffset;
        int codeSize;
        bool fat;
        switch (first & 3)
        {
            case 2: // tiny header
                codeSize = first >> 2;
                codeOffset = bodyOffset + 1;
                fat = false;
                break;
            case 3: // fat header
            {
                var flagsAndSize = BitConverter.ToUInt16(dll, bodyOffset);
                if ((flagsAndSize & 3) != 3) throw new InvalidOperationException("方法体头格式非法。");
                var headerDwords = (flagsAndSize >> 12) & 0xF;
                if (headerDwords < 3) throw new InvalidOperationException("fat 方法头长度非法。");
                codeSize = (int)BitConverter.ToUInt32(dll, bodyOffset + 4);
                codeOffset = bodyOffset + headerDwords * 4;
                fat = true;
                break;
            }
            default:
                throw new InvalidOperationException("目标方法不是合法的 IL 方法体。");
        }

        if (codeSize <= 0 || codeSize > 4096 || codeOffset + codeSize > dll.Length)
            throw new InvalidOperationException($"方法体长度异常: {codeSize}");

        // 判定已打补丁：CodeSize 已被压到 2，且 IL 正好是 ldc.i4.0; ret。
        var isPatched = codeSize == 2 && dll[codeOffset] == 0x16 && dll[codeOffset + 1] == 0x2A;
        return new MethodLocation
        {
            BodyOffsetInCore = bodyOffset,
            CodeOffsetInCore = codeOffset,
            CodeSize = codeSize,
            HasFatHeader = fat,
            IsPatched = isPatched
        };
    }

    private static int RvaToFileOffset(PEHeaders headers, int rva)
    {
        foreach (var section in headers.SectionHeaders)
        {
            var end = section.VirtualAddress + Math.Max(section.VirtualSize, section.SizeOfRawData);
            if (rva < section.VirtualAddress || rva >= end) continue;
            var delta = rva - section.VirtualAddress;
            if (delta >= section.SizeOfRawData)
                throw new InvalidOperationException("目标 RVA 落在未映射到文件的节区里。");
            return section.PointerToRawData + delta;
        }

        throw new InvalidOperationException($"无法把 RVA 0x{rva:X} 映射到文件偏移。");
    }

    // ------------------------------------------------------------------ helpers

    private static string RequirePath(string[] args, int index)
    {
        if (args.Length <= index || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException("缺少路径参数。用 --help 查看用法。");
        return args[index];
    }

    private static string Hex(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder(data.Length * 3);
        for (var i = 0; i < data.Length; i++)
        {
            if (i > 0) builder.Append(' ');
            builder.Append(data[i].ToString("X2"));
        }

        return builder.ToString();
    }
}
