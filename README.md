# PCL-CE 中国大陆区域限制绕过工具

针对**已经构建好的** PCL-CE 产物，不改源码、不重新编译启动器：修正产物里 `PCL.Core.dll`
的一个方法，使中国大陆区域限制失效。提供两种形态：

- **双击运行版（推荐）**：`dist/dynamic/PCLDynamicBypass.exe` —— 每次启动前自动打补丁、
  拉起同目录的 PCL、PCL 退出后自动还原原文件（异常退出下次启动自动修复）。
- **静态补丁工具**：`dist/tool/PCLRegionBypass.exe` —— 一次性把补丁写死进 exe
  （或对目录构建直接 patch `PCL.Core.dll`），适合长期使用。

本目录包含：

```
bypass/
├─ PCLRegionBypass.csproj      # 静态补丁工具工程（net10.0-windows）
├─ Program.cs                  # 静态工具实现（bundle 解析 / IL 定位 / patch / 验证）
├─ README.md                   # 本文件
├─ dynamic/
│  └─ PCLDynamicBypass/        # 双击运行版工程（启动前临时补丁 + 退出还原）
│     ├─ PCLDynamicBypass.csproj
│     ├─ Program.cs
│     └─ app.manifest
└─ dist/
   ├─ dynamic/                 # 双击运行版成品（放 4 个文件到 PCL 同目录，双击 exe）
   ├─ tool/                    # 静态补丁工具
   └─ launcher/                # 已永久打好补丁的 "Plain Craft Launcher 2.exe"（23,785,032 B）
```

> 合规提示：这个区域限制是上游为面向中国大陆的合规/授权考虑加的（#3673 / #3676）。
> 请只在你拥有或获授权的构建 / 测试环境里使用；对外分发"绕过版"可能违反 Minecraft
> 在中国的区域授权条款或相关法律，风险由使用者承担。

---

## 0. 双击运行版（推荐）

### 用法

1. 把 `bypass/dist/dynamic/` 里的 **4 个文件**（`PCLDynamicBypass.exe`、
   `PCLDynamicBypass.dll`、`PCLDynamicBypass.deps.json`、`PCLDynamicBypass.runtimeconfig.json`）
   复制到 PCL 启动器所在目录（和 PCL 的 exe 放一起）；
2. 双击 `PCLDynamicBypass.exe`。

### 目标选择规则

启动器不再写死文件名，而是扫描**本程序所在目录**：

1. 取所有 `*.exe`，**排除本程序自身**（按完整路径 + 文件名排除，所以
   `PCLDynamicBypass.exe` 自己虽然带 "PCL" 也不会被当成目标）；
2. 在剩下的 exe 里找**文件名包含 `PCL`** 的（不区分大小写，如 `PCL.exe`、
   `PCL2_test.exe`、`PCL-CE.exe`）；
3. **恰好 1 个** → 对该 exe 打补丁并启动；
4. **0 个** → 终止启动并提示；
5. **2 个及以上** → 视为歧义，**终止启动**并弹窗列出所有候选，不修改任何文件；
6. 兼容例外：若目录里没有任何带 "PCL" 字样的 exe，但存在官方命名
   `Plain Craft Launcher *.exe` 且恰好一个，则回退处理它（官方产物名不含 "PCL"）。

启动器会正常打开；用它安装/下载/补全/整合包时不再被中国大陆区域限制拦截。PCL 退出后，
该 exe 自动恢复成完全未修改的状态。

### 它做了什么

```
双击 PCLDynamicBypass.exe
  ├─ 扫描同目录 exe（排除自身），必须恰好一个文件名含 "PCL"
  ├─ 解析该 exe 的单文件 bundle → 取出内嵌 PCL.Core.dll（只在内存里）
  ├─ 在内存里生成补丁（把 get_IsRestricted 改成 return false）
  ├─ 备份原 exe 到 *.pclbypass.bak，写标记 *.pclbypass.marker
  ├─ 把补丁写入 exe（原子替换，杀软占用时自动重试/降级）
  ├─ 启动该 exe（原文件，同目录、同数据目录）
  ├─ 等待 PCL 全部退出（含它自己重启出的新进程）
  └─ 校验后把原文件写回，删除备份/标记
```

- 正常退出：exe 恢复到原始 SHA256，目录里不留任何额外文件。
- PCL 崩溃 / 启动器被强杀：`.bak` + `.marker` 会留在原地，**下次双击启动器时自动先还原**，
  再重新补丁。
- 同一时间重复双击：第二次检测到"已处于补丁状态"，直接拉起 PCL（由第一个实例负责还原）。
- 日志：`%TEMP%\PCLDynamicBypass\launcher.log`（每次启动都会记录哈希与还原结果）。

### 为什么不是"纯内存补丁"

先试过两种纯内存方案，都不可靠：

1. 启动钩子（`DOTNET_STARTUP_HOOKS`）里把补丁版 `PCL.Core` 预加载进 ALC：单文件 bundle
   的解析会先命中 bundle 里的原版程序集，补丁版不生效；
2. 启动钩子里等 `PCL.Core` 加载后直接改内存镜像的 IL：bundle 内嵌程序集是**只读文件映射**，
   `VirtualProtect` 返回 `ERROR_INVALID_PARAMETER`（err 87）；改 JIT 后的原生代码入口也
   会被 `Evaluate` 被内联/分层编译绕过。

因此改为"启动前写盘、退出还原"：效果上仍是双击即用、无需手动 patch，且**运行结束后文件
与原始版本逐字节一致**。若目录不可写（如装在 Program Files 且无权限），会弹窗提示。

### 验证记录（本机，2026-10-20）

对 `~/Downloads/PCL2_CE_CI_x64/Plain Craft Launcher 2.exe`（SHA256 `5B7EB2B6…EC2BE3D`）：

- 启动器日志显示 `patchedHash=511BB0A0…F18C5933`，与静态补丁工具生成的补丁版 exe 哈希
  **完全一致**（证明两者打的是同一处 3 字节补丁）；
- 运行期间轮询文件哈希：`5B7EB2B6…`（原版）→ `511BB0A0…`（补丁态）→ 结束后回到
  `5B7EB2B6…`；
- 连续 3 次运行：`PatchedSeen=True / Restored=True / Leftovers=0`；
- 模拟"上次异常退出"（预置 patched exe + `.bak` + `.marker`）：启动器先自动还原，再正常
  补丁、拉起、还原，最终哈希回到原版，无残留。

目标选择规则的测试结果：

| 场景 | 结果 |
|---|---|
| 目录只有一个 `PCL.exe`（+ 本程序，本程序名也含 "PCL"） | 正确选中 `PCL.exe`，补丁/启动/还原成功，无残留 |
| 目录有 `PCL-A.exe` 和 `PCL-B.exe` | 终止启动（exit 1），两个文件哈希均未变，无残留 |
| 目录没有含 "PCL" 的 exe | 终止启动（exit 1） |
| 目录只有官方命名 `Plain Craft Launcher 2.exe` | 回退选中它，补丁/启动/还原成功 |

---

## 1. 它到底改了什么

区域判定的调用链：

```
RegionPolicy.Evaluate()  ──►  RegionAssessment { Classification, Reason }
                                      │
                                      └─ bool IsRestricted  // Mainland / Unknown 时为 true
RestrictedFeaturePolicy.Check(feature)
      └─ if (!Assess().IsRestricted) return true;   // 放行
      └─ 否则提示 "Region.Feature.Restricted" 并拦截
```

`RegionAssessment.IsRestricted` 是会编译成方法 `PCL.Core.App.Region.RegionAssessment::get_IsRestricted`
的表达式属性。工具只做一件事：

> 把 `get_IsRestricted` 的方法体换成 `ldc.i4.0; ret`（恒返回 false），
> 并把方法头里的 `CodeSize` 从 26 改成 2。

这样 `Check()` / `IsRestricted` / `ThrowIfRestricted()` 全部放行，安装、下载、补全、重置、
整合包（含在线/自动安装）等所有被区域拦截的入口都恢复。

**没有**修改：区域信号采集（GeoName/注册表/时区/语言）、`RegionPolicy.Evaluate` 的判定逻辑、
字符串资源、配置项、自更新逻辑。

### 为什么不用 NOP 填满原长度？

一开始的写法是保留 26 字节方法体长度，写 `16 2A` 后用 `nop` 填满剩余 24 字节。
实测 .NET 10 JIT 对此抛 `InvalidProgramException`（"Common Language Runtime detected an
invalid program"），所以最终改为**同时把方法头 CodeSize 缩到 2**。这也是为什么最终只改了
**3 个字节**：

| 文件偏移（exe） | 原值 | 新值 | 含义 |
|---|---|---|---|
| 20,759,696 | `1A` | `02` | 方法头 CodeSize：26 → 2 |
| 20,759,704 | `02` | `16` | IL 第 1 字节：`ldarg.0` → `ldc.i4.0` |
| 20,759,705 | `28` | `2A` | IL 第 2 字节：`call` → `ret` |

没有改动任何元数据（Metadata token、RVA、字符串堆、方法表都不动）：新方法体不引用任何
token，后续方法的 RVA 由元数据固定，所以缩短方法体只在原地留下不可达字节，不影响其它方法。

### 单文件 bundle 内嵌 DLL 是怎么定位的

该构建是 **.NET 单文件 bundle（格式 6.0）**：`Plain Craft Launcher 2.exe` 尾部是 bundle header
+ manifest，93 个内嵌文件里包含 `PCL.Core.dll`（offset 20,447,232 / size 3,287,040，未压缩）。
定位步骤：

1. 在 exe 中搜索 apphost 里的 **32 字节 bundle 签名**
   （`8B 12 02 B9 6A 61 20 38 72 7B 93 02 …`，其前 8 字节是 bundle header 的文件偏移）；
2. 解析 header（MajorVersion=6、MinorVersion=0、NumFiles=93、BundleID、deps/runtimeconfig 位置、flags）
   与 manifest 条目（Offset/Size/CompressedSize/Type/Name）；
3. 找到 `PCL.Core.dll` 条目，把该区段当作独立 PE 用 `System.Reflection.Metadata` 解析；
4. 按 `TypeDef → MethodDef("get_IsRestricted") → RVA → 节区 → 文件偏移` 定位方法体，
   读 tiny/fat 方法头，写入补丁。

如果构建启用了 `EnableCompressionInSingleFile`（条目 `CompressedSize != 0`），工具会报错拒绝
（不会写坏文件）；目录构建（磁盘上的 `PCL.Core.dll`）则直接 patch 该 DLL。

---

## 2. 用法

工具已发布：`bypass/dist/tool/PCLRegionBypass.exe`（需要 .NET 10 桌面运行时）。
想自己编译：`dotnet build bypass/PCLRegionBypass.csproj -c Release`。

```powershell
# 1) 只读分析：识别形态、列出 bundle 条目、打印目标方法当前 IL
.\PCLRegionBypass.exe inspect "C:\Path\To\Plain Craft Launcher 2.exe"

# 2) 打补丁（默认在目标旁生成 .pclregionbak 备份；--no-backup 可关闭）
.\PCLRegionBypass.exe patch "C:\Path\To\Plain Craft Launcher 2.exe"
#    也可以直接传安装目录：patch "C:\Path\To\PCL"

# 3) 验证：反射加载 PCL.Core.dll，打印三种分类的 IsRestricted
.\PCLRegionBypass.exe verify "C:\Path\To\PCL.Core.dll"

# 4) 诊断：用目标 DLL 真实调用 RegionPolicy，打印本机信号与判定（不修改任何东西）
.\PCLRegionBypass.exe diagnose "C:\Path\To\PCL.Core.dll"

# 5) 导出单文件 bundle 里的条目（比对原版/补丁版用）
.\PCLRegionBypass.exe extract "C:\Path\To\Plain Craft Launcher 2.exe" PCL.Core.dll .\PCL.Core.dll
```

命令是幂等的：已打过补丁的文件再跑 `patch` 会输出 `[SKIP]` 并退出码 0。

**回滚**：把 `Plain Craft Launcher 2.exe.pclregionbak` 覆盖回原文件，或重新解压官方包。

---

## 3. 本机实测记录（2026-10-20）

靶子：`~/Downloads/PCL2_CE_CI_x64/Plain Craft Launcher 2.exe`（23,785,032 B；bundle 6.0，
93 条目，BundleID `tvcI6fvREuFG`，内嵌 `PCL.Core.dll` 3,287,040 B）

```
原版    SHA256 5B7EB2B6060A408227230FD976185189BA689441FB904B521F06FDC81EC2BE3D
补丁版  SHA256 511BB0A07679EB4F00DFDFD70AE1D95D64208BA53D2B273FA6060EC3F18C5933
```

### 3.1 原版行为

```
[目标方法] PCL.Core.App.Region.RegionAssessment::get_IsRestricted
  原始 IL: 02 28 8E 16 00 06 0A 06 2C 08 2B 00 06 18 2E 02 2B 04 17 0B 2B 02 16 0B 07 2A
  (26 字节，fat 方法头，等价于 `Classification is Unknown or Mainland`)

verify（原版 DLL）:
  Classification=Unknown  IsRestricted=True
  Classification=Other    IsRestricted=False
  Classification=Mainland IsRestricted=True
```

### 3.2 本机区域判定（原版 DLL，`diagnose`）

```
GeoName = CN            TimeZoneId = China Standard Time
PreferredUiLanguages = zh-CN, en-US
UserLocaleName = zh-CN
Classification = Mainland
Reason         = geo-cn
IsRestricted   = True
```

即：在这台中国大陆机器上，不 patch 的话安装功能确实会被拦。

### 3.3 补丁后

```
  方法头 CodeSize: 26 -> 2
  补丁 IL: 16 2A
  (exe 仅 3 字节变化，见上表)

verify（补丁后 DLL）:
  Classification=Unknown  IsRestricted=False
  Classification=Other    IsRestricted=False
  Classification=Mainland IsRestricted=False
  [RESULT] 所有分类 IsRestricted=False —— 补丁生效。
```

同时测试了**目录构建**路径（源码当天构建的 `PCL.Core.dll`，3,145,216 B，IL 22 字节）：
patch 后同样全部 False，说明工具不依赖具体版本的方法体长度/指令序列。

### 3.4 启动冒烟测试

补丁版 exe 启动后正常进入生命周期服务（`PCL\Log\LastPending.log` 可见 Startup 日志），
随后因**测试沙箱**禁止写 `%LOCALAPPDATA%\PCLCE\instance.lock` 而在单例服务处退出（exit 1）；
**原版 exe 在同一沙箱下表现完全相同**，与补丁无关。正常桌面环境下不会遇到这个问题。

---

## 4. 局限与注意

1. **只关掉"区域拦截"**：`Lang.IsFeaturesUnrestricted` / `Lang.IsChineseMainland` 控制的
   "非中国大陆隐藏特供功能"（MC 百科入口、翻译、中文搜索等）属于另一套逻辑（取决于启动器
   语言/时区），本工具不会改变它们。
2. **自更新会覆盖补丁**：启动器更新会用官方 exe 替换文件，更新后需重新 `patch`。
3. **不支持的构建形态**：单文件压缩（`EnableCompressionInSingleFile`）会被拒绝；
   ReadyToRun 构建下 IL 可能被原生代码抢先，补丁可能不生效（当前 CI 构建未启用这些，本工具
   对目标方法做定位校验，找不到/形态不符会报错而不是瞎写）。
4. **版本差异**：工具按"类型 + 方法名"定位，不依赖固定偏移；只要新版本仍保留
   `PCL.Core.App.Region.RegionAssessment.get_IsRestricted` 就能用。若上游把判定改成
   `Evaluate` 内直接返回，或方法被内联重命名，需要相应调整目标。
5. **只改本地产物**：不涉及服务器、不代理网络；判定本来就全在客户端。

## 5. 备选方案（不想改二进制时）

区域判定全部来自本机设置，改设置也能绕过（但会影响整台机器，且需要管理员）：

1. 设置 → 时间和语言 → 区域 → 国家或地区：改成非中国（使 `GetUserDefaultGeoName` 返回 US 等）；
2. 区域格式 / 首选语言改成非 `zh-CN`（避免"中国时区 + 中文区域"组合命中）；
3. 删除 `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Control Panel\DeviceRegion`
   的 `DeviceRegion` 值（这是开机 OOBE 区域，写注册表需要管理员）。

注意判定顺序是"任一中国正信号即命中"：只改国家或地区、但保留中国时区 + 中文区域/语言，
仍会被 `china-time-zone-and-locale` 判成 Mainland；改时区 + 语言 + 地区 + 设备区域四者才稳。
