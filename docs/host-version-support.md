# CAD 宿主版本支持（官方 AutoCAD 2020–2025）

**范围决定（2026-09-24，用户拍板）**：只支持**官方版 AutoCAD 2020–2025**。2019 及更早、AutoCAD LT、浩辰/中望/CAXA 等第三方 CAD 一律不做扩展；不做 DWG SDK 重写。

**当前状态**：2020（net47）、2021–2024（net48）、2025（net8）**全部编译通过（0 error）**，共 6 个目标一次构建成功。剩下的只有"打包 + 真机验收"两步，见第 5 节。

## 1. 根因：三层硬绑定（已逐层解开）

| 绑定层 | 原状 | 现状 |
|---|---|---|
| 插件运行时 | 只有 `net8.0-windows` 一份 DLL，AutoCAD 2024 及更早是 .NET Framework 宿主，`_.NETLOAD` 直接失败 | csproj 多目标：`-p:AutoCADRelease=2020..2025` 自动选 `net47`/`net48`/`net8.0-windows`；2020–2024 的引用程序集来自官方 `AutoCAD.NET` NuGet 包，**本机不装那个版本也能编译** |
| 宿主发现 | 硬编码 `C:\Program Files\Autodesk\AutoCAD 2025`、只扫注册表 `R25.0`、只认 {2025,2027} | 枚举 HKLM+HKCU 全部发行版，按"打包插件能否加载"优先选择；非默认安装路径（如 `E:\AUTOCAD2025\AutoCAD 2025`）从注册表 `Release` 值解析 |
| 打包/部署 | 只有一份扁平 `assets/plugin`，部署到 `CadTranslation2025.bundle` | `assets/plugin/<R-release>/` 按版本打包，部署到 `CadTranslation<year>.bundle\...\package-<内容哈希>`；**任何非 2025 版本都不会回退去加载 2025 的 DLL** |

必须区分（用户最常混淆）：

- **图纸文件版本**（DWG 2000/2004/2007…）：本来就支持。原生层用 `db.OriginalFileVersion` 保存（`NativeDrawing.cs:34`、`ReplaceDrawingImporter.cs:304`），输入什么格式就存回什么格式。
- **宿主程序版本**（本机装的 AutoCAD 是哪一年）：这才是唯一阻断点，也是本文的范围。

## 2. 支持矩阵（决定后的口径）

| 宿主 | 无头执行 | 插件运行时 | 编译状态 | 打包/验收 |
|---|---|---|---|---|
| AutoCAD 2025 | accoreconsole | .NET 8 | ✅ 0 error（本机 `E:\AUTOCAD2025`） | ✅ 已打包并在真机跑通（当前是 Debug 构建，待换 Release，见 5.3） |
| AutoCAD 2024 | accoreconsole | .NET Framework 4.8 | ✅ 0 error（`AutoCAD.NET 24.3.0`） | ⬜ 待打包 + 待真机验收 |
| AutoCAD 2023 | accoreconsole | .NET Framework 4.8 | ✅ 0 error（`24.2.0`） | ⬜ 同上 |
| AutoCAD 2022 | accoreconsole | .NET Framework 4.8 | ✅ 0 error（`24.1.51000`） | ⬜ 同上 |
| AutoCAD 2021 | accoreconsole | .NET Framework 4.8 | ✅ 0 error（`24.0.0`） | ⬜ 同上 |
| AutoCAD 2020 | accoreconsole | .NET Framework 4.7 | ✅ 0 error（`23.1.0`） | ⬜ 同上 |
| AutoCAD 2019 及更早 | — | — | ❌ 不做（`out-of-window`） | 运行时会明确告知"超出官方支持窗口 2020–2025"，并建议换宿主或走集中执行机 |
| AutoCAD LT / 浩辰 / 中望 / CAXA | 无 .NET API 或各家私有 API | — | ❌ 不做 | 属于"厂商"问题，不是"版本"问题 |

运行侧分级由 `host_support()` 给出：`supported`（已打包）→ `port-required`（在窗口内但尚未打包）→ `build-required`（2027，需单独 .NET 10 构建）→ `out-of-window`（2013–2019）→ `unsupported`（2012 及更早无 accoreconsole、2004 及更早无 .NET API）。

## 3. 实现结构（改动都在哪里）

**原生侧（`src/cad`）**

- `Directory.Build.props`：宿主矩阵（`AutoCADRelease` → `AutoCADNetVersion` + `CadHostFramework`）；net4x 统一挂 `System.Text.Json 8.0.5`、`Microsoft.NETFramework.ReferenceAssemblies 1.0.3`，并把 `Build/NetFxPolyfills.cs`、`Build/NetFxGlobalUsings.cs` 链接进**每个**项目。
- `Directory.Build.targets`：net4x 下 `Nullable=annotations`（.NET Framework 与 AutoCAD 2020–2024 的引用程序集没有可空标注，严格分析会把每个 BCL/AutoCAD 返回值都判成 maybe-null；.NET 8 仍然全严格）。**必须放在 .targets**：`.props` 阶段 `$(TargetFramework)` 还是空的，条件不成立。
- `Build/NetFxPolyfills.cs`：一处集中补齐 BCL 差异——`IsExternalInit`、`CallerArgumentExpression`、`System.Index/Range`（internal，避免跨程序集 CS0436）、`ThrowIfNull`/`ThrowIfNullOrWhiteSpace`/`Clamp`/`IsFinite`/`IsAsciiLetter*`/`ToHexString`/`IsPathFullyQualified`/`GetRelativePath`（**无条件编译**，net8 与 net4x 走同一条代码路径、同一套语义），以及 net4x 专属扩展 `Contains(char)`/`Split(char,…)`/`Replace(…,StringComparison)`/`Order`/`DistinctBy`/`TryAdd`/`Deconstruct`/`GetValueOrDefault`/`Zip(second)`/`FirstOrDefault(default)`/`ToHashSet`（`#if !NET48_OR_GREATER`）/`Append`+`Prepend`（`#if !NET48_OR_GREATER`，net47 才缺）。
- `CadTranslation.AutoCAD2025.csproj`：`AutoCADDir` 优先用本机宿主程序集，否则用官方 `AutoCAD.NET`/`.Core`/`.Model` 引用包（`ExcludeAssets=runtime`，只编译不带走运行时）；两者都没有就直接报错，绝不静默用错版本的程序集编译。

**运行侧（`scripts/cad_translate.py`）**

- `AUTOCAD_RELEASE_YEARS`（R 键 → 年份）、`YEAR_TO_RELEASE`、`BUILDABLE_LEGACY_FRAMEWORKS`（2020→net47，2021–2024→net48）、`SUPPORTED_HOST_FIRST_YEAR=2020`。
- `registry_hosts()` / `host_compatibility()` / `host_compat_report()` / `shipped_plugin_years()` / `packaged_plugin_dir()` / `build_hint()`；新命令 **`host-compat`**。
- `doctor` 增加 `hostCompatibility`、`blockers`、`packagedPluginAvailable`、`packagedPluginDir`、`buildHint`；`autocad_release()` 的错误信息直接带出下一步命令。

```
python scripts\cad_translate.py host-compat
```

实测分级：2018 → `out-of-window`（不可构建）；2020 → `port-required` + `net47`；2021/2024 → `port-required` + `net48`；2025 → `supported`。

## 4. 构建与打包命令

编译某个宿主版本（不需要本机装那个版本，走官方 NuGet 引用包）：

```
dotnet build src/cad/CadTranslation.AutoCAD2025/CadTranslation.AutoCAD2025.csproj -c Release -p:AutoCADRelease=2024
```

打包（把输出 DLL 放进按版本目录，运行侧立刻把该宿主识别为 `supported`）：

```
copy src\cad\CadTranslation.AutoCAD2025\bin\Release\net48\CadTranslation.*.dll assets\plugin\R24.3\
python scripts\cad_translate.py host-compat     # 2024 应显示 support=supported
```

本机装了目标版本时可改用本机程序集（更快、与真机完全一致）：追加 `-p:AutoCADDir="<安装根>"`；2025 用 `-p:Platform=x64`。

## 5. 还剩什么

1. **打包 2020–2024**：五个版本各构建一次并放进 `assets/plugin/R23.1|R24.0|R24.1|R24.2|R24.3/`（`.deps.json` 只有 net8 有，net4x 没有，属正常）。
2. **真机验收**：每个版本至少跑通一次 `export → import → audit-summary → 可视化复核`。**编译通过不等于运行通过**——net4x 走的是 AutoCAD 2020–2024 的托管 API，`MText`/`Table`/`Editor` 的运行时行为、accoreconsole 的退出码与脚本差异只能在真机（或虚拟机矩阵）上确认。验收前不得对外声称"已支持"。
3. **2025 的 Release 重打包 + 一次全量回归**（按用户要求，等另外两个任务结束后做）：`assets/plugin` 现在是 Debug 构建，且三个 DLL 来自两次不同构建（Contracts/Core 14:18–14:19、AutoCAD2025 14:54），布局/碰撞热路径比 Release 慢；重打包会改变部署包内容哈希，必须配一次全量图纸回归，并在 `SKILL.patch.md` 记录新哈希。
4. **不要做的事**：不再扩到 2019 及更早；不引第三方 NuGet 补 BCL（部署只带 `PLUGIN_FILES` 三个 DLL，多一个运行时依赖就要改部署清单与哈希校验）；不在业务文件里散落 `#if`，框架差异统一进 `Build/NetFxPolyfills.cs`。

## 6. 移植记录（152 → 0 error）

| 类别 | 处理方式 | 处数 |
|---|---|---|
| `ArgumentNullException.ThrowIfNull` / `Math.Clamp` / `double.IsFinite` / `Convert.ToHexString` / `Path.IsPathFullyQualified` / `Path.GetRelativePath` / `char.IsAsciiLetter*` | 集中 shim + 机械改写为裸调用（net8/net4x 同一路径） | 90 |
| `Contains(char)`、`Split(char,…)`、`Replace(…,StringComparison)`、`Order`、`DistinctBy`、`TryAdd`、`Deconstruct`、`GetValueOrDefault`、`Zip`、`FirstOrDefault(default)`、`ToHashSet`、`Append` | net4x 扩展方法，**调用点零改动** | — |
| `System.Index`/`System.Range`、`IsExternalInit`、`CallerArgumentExpression` | 源码 polyfill（internal，避免跨程序集冲突） | — |
| `SHA256.HashData`（Core/Hashing.cs、XrefScanner.cs） | `#if NETFRAMEWORK` 走 `SHA256.Create().ComputeHash` | 4 |
| `File.Move(…, overwrite)`（AtomicFile.cs） | net4x 走 `MoveFileEx(MOVEFILE_REPLACE_EXISTING)`；**不能用"先删后移"**，那会在中间留下"目标文件不存在"的窗口，违背原子写语义 | 1 |
| `StreamWriter(…, leaveOpen:)` 缺 `bufferSize` | 补 `1024`（与 BCL 默认一致，两个框架都合法） | 2 |
| `Enum.GetNames<T>()` | 改 `Enum.GetNames(typeof(T)).Cast<string>()` | 1 |
| `MatchCollection.Select` | 前置 `.Cast<Match>()`（net4x 的 `MatchCollection` 不是 `IEnumerable<Match>`） | 2 |
| `normalized.AsSpan(…)` 传入 `double.TryParse` | `#if` 分支，net4x 用 `Substring` | 1 |
| 可空告警（AutoCAD/BCL 引用程序集无标注） | `Directory.Build.targets` 里 net4x 降为 `annotations`；个别真需标注处用 `!` | 12 |
| **顺带修掉一个真实 bug** | `BilingualFixedLabelPolicy.cs:289` 的 net4x 回退分支写成 `MeaningfulEnglishWordValue`（字段实际叫 `MeaningfulEnglishWordPatternValue`），该分支从未被编译过所以一直没暴露 | 1 |

验证（2026-09-24 16:40）：插件项目 `-p:AutoCADRelease=2020|2021|2022|2023|2024|2025` **六个目标全部 0 error**（2020 走 net47、2021–2024 走 net48 且引用程序集来自官方 `AutoCAD.NET` NuGet 包、2025 走 net8 + 本机宿主）；`CadTranslation.Core` 在 net48/net47/net8.0 均 0 error；原生 Core 测试运行器 **exit 0、218 PASS / 0 FAIL**；Python 115 个用例除 3 个既有"打包产物是 Debug 构建"的哈希断言外全部通过（该断言会在 5.3 重打包后转绿）。net8 构建无 CS86xx 可空告警（`ThrowIfNull` shim 保留了 `[NotNull]` 语义，net4x 用内置 polyfill 属性）。

## 7. 本轮踩到的坑（写进规则，避免重犯）

1. **`global.json` 把 SDK 钉在 8.0.424（C# 12）**：`using ReadOnlySet<T> = IReadOnlySet<T>;` 这种**开放泛型 using 别名不被支持**，会让整个插件项目 6 个 error、任何重建都失败（`assets/plugin` 无法从源码再生）。已改为按元素类型闭合的别名。任何新的框架抽象都必须 C# 12 合法。
2. **`Directory.Build.props` 里读不到 `$(TargetFramework)`**：属性在 props 阶段求值（此时项目体还没设 TFM），ItemGroup 却在属性之后求值——所以同一个条件在 ItemGroup 上生效、在 PropertyGroup 上不生效。TFM 相关的**属性**必须放 `Directory.Build.targets`。
3. **net47 与 net48 的 BCL 不一样**：`Enumerable.ToHashSet`、`Append`/`Prepend` 在 net48 有、net47 没有；无脑加扩展会在 net48 上变成 CS0121 二义性。按 `#if !NET48_OR_GREATER` 收窄。
4. **polyfill 类型必须 internal**：`System.Index`/`Range` 若声明为 public，会随 Contracts 程序集导出，Core 再链接同一文件就触发 CS0436 冲突。
5. **`TreatWarningsAsErrors=true`**：重复 `PackageReference`（NU1504）、可空告警都会直接变成构建失败——依赖只能在一处声明。
