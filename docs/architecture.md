# 架构说明（Architecture）

> 这份文档只回答「代码怎么放」。工具本身的行为看 [`tool-catalog.md`](tool-catalog.md)，插件机制看 [`plugin-model.md`](plugin-model.md)，输入模型看 [`context-model.md`](context-model.md)。

---

## 一、设计目标

架构为「**工具集合会持续变多，且每个工具都能独立存在**」服务，不是为「框架很完整」服务。由此推出四条约束：

1. **工具 = 插件**。加工具是加一个插件项目，不是加一个类。
2. **宿主不知道任何具体工具**。宿主的代码里不应出现 `Json`、`Ocr`、`Clipboard` 这类词。
3. **插件只能看到 SDK**，看不到宿主内部结构。
4. **工具崩了不能拖垮壳**（托管异常范围；原生崩溃是已知限制，见 plugin-model）。

---

## 二、项目结构

### 2.1 宿主（4 个项目）

```text
Sparrow/
├─ Sparrow.slnx
├─ Directory.Build.props          公共编译设置
├─ Directory.Packages.props       集中包版本（唯一版本来源）
├─ global.json                    锁定 SDK 10.0.103
│
├─ src/
│  ├─ Sparrow.Plugin.Sdk/         ← 契约包，对外发布，向后兼容承诺
│  │  ├─ ITool.cs
│  │  ├─ IToolHost.cs
│  │  ├─ ToolDescriptor.cs  ToolParameter.cs
│  │  ├─ ToolContext.cs     ToolResult.cs
│  │  ├─ Inputs/            TextInput, ImageInput, FileSetInput, EmptyInput
│  │  ├─ Payloads/          Text, Svg, Image, FileSet, Table
│  │  ├─ ToolAction.cs  ToolError.cs  ToolProgress.cs
│  │  └─ Enums/             InputShape, OutputShape, ToolSource, PlatformCapability
│  │
│  ├─ Sparrow.Core/               ← 宿主领域，可自由重构，不对外
│  │  ├─ History/           HistoryEntry
│  │  ├─ Settings/          AppSettings, PluginSettings
│  │  └─ Ports/             IHistoryStore, ISettingsStore, ISecretStore, IAppPaths
│  │
│  ├─ Sparrow.App/                ← Avalonia 壳 + ViewModel + 服务
│  │  ├─ Program.cs  App.axaml(.cs)
│  │  ├─ Shell/
│  │  │  ├─ TrayService.cs
│  │  │  ├─ HotkeyService.cs
│  │  │  ├─ SingleInstanceGuard.cs
│  │  │  ├─ WindowNavigator.cs
│  │  │  ├─ ClipboardMonitor.cs
│  │  │  └─ ScreenshotPicker.cs
│  │  ├─ Plugins/                 ← 插件子系统的宿主侧
│  │  │  ├─ PluginScanner.cs      扫描目录、读清单（不加载程序集）
│  │  │  ├─ PluginManifest.cs
│  │  │  ├─ PluginLoadContext.cs  ALC 隔离
│  │  │  ├─ PluginLoader.cs       加载 / 卸载 / 故障处理
│  │  │  ├─ PluginHost.cs         IToolHost 实现
│  │  │  └─ ToolRegistry.cs       元数据索引 + 搜索
│  │  ├─ ViewModels/
│  │  ├─ Views/  Controls/  Themes/
│  │  ├─ Services/
│  │  │  ├─ ToolScheduler.cs
│  │  │  ├─ ContextBuilder.cs
│  │  │  ├─ SqliteHistoryStore.cs
│  │  │  └─ JsonSettingsStore.cs
│  │  └─ Platform/Windows/        ← M1 起才拆独立项目
│  │     ├─ Win32Hotkey.cs  Win32Clipboard.cs  Win32Screenshot.cs
│  │     └─ Native/User32.cs, Gdi32.cs
│  │
│  └─ Sparrow.Ocr.Host/           ← 独立进程，只被 OCR 插件通过 stdio 调用
│     └─ Program.cs
│
├─ plugins/                        ← 每个工具一个插件项目
│  ├─ Sparrow.Plugin.Clipboard.History/
│  │  ├─ Plugin.cs                  ITool 实现
│  │  ├─ plugin.json
│  │  └─ Sparrow.Plugin.Clipboard.History.csproj
│  ├─ Sparrow.Plugin.Json.Format/
│  ├─ Sparrow.Plugin.Ocr.Region/    （region + image，共用模型）
│  └─ …
│
├─ tests/
│  └─ Sparrow.Tests/
│
├─ docs/
└─ build/
```

### 2.2 项目数量怎么控制

```text
宿主  4 个（Sdk / Core / App / Ocr.Host）
插件  每个工具 1 个（M0 是 1 个，M3 是 13 个）
```

听起来项目会变多，但这和「20 个按概念层次拆的项目」是两回事：

| | 按层次拆（已否决） | 按插件拆（现在） |
|---|---|---|
| 项目边界依据 | 概念（Application / Presentation / Infrastructure） | 交付单元（一个工具） |
| 每个项目的内容 | 薄，但互相都要引用 | 厚，只依赖 Sdk |
| 加功能的成本 | 跨 3–4 个项目改代码 | 加一个目录 |
| 删功能的成本 | 留下死代码和空壳项目 | 删一个目录 |
| 编译时间 | 线性增长 | 可增量（未变的插件不重编译） |

**代价**：每个插件项目有约 3–5 个文件是样板（csproj / plugin.json / 一个空类）。

**对策**：提供 `dotnet new sparrow-tool` 模板，一条命令生成骨架。见 [`tool-development.md`](tool-development.md)。

---

## 三、依赖规则（编译器强制）

```text
Sparrow.Plugin.Sdk  → 不依赖任何项目（只允许 BCL + Microsoft.Extensions.*.Abstractions）
Sparrow.Core        → 依赖 Sdk
Sparrow.App         → 依赖 Sdk；另以 ReferenceOutputAssembly=false 引用 plugins/*，
                       只为构建顺序与产物复制，不是运行时依赖
Sparrow.Ocr.Host    → 不依赖任何 Sparrow 项目（只靠 stdio）
plugins/*           → 只依赖 Sdk
tests/*             → 依赖全部
```

### 3.1 CI 断言（不是靠自觉）

```text
1. Sparrow.Plugin.Sdk 的 ProjectReference 数量必须 = 0
2. plugins/* 的 ProjectReference 只能是 Sparrow.Plugin.Sdk
3. plugins/* 的 csproj 里不得出现 Avalonia.PackageReference
4. Sparrow.App 的源码里不得出现具体工具名（grep 关键字检查）
5. Sparrow.App 对 plugins/* 的引用必须是构建期依赖（ReferenceOutputAssembly=false、Private=false）
```

第 4 条最有用。宿主的 `PluginScanner` / `ToolRegistry` 里如果出现 `"json"` 这种字符串，说明边界破了。

```powershell
# 示例：宿主代码不应出现工具名
$hits = Select-String -Path src/Sparrow.App/**/*.cs -Pattern 'sparrow\.(json|ocr|clipboard)\.'
if ($hits) { throw "宿主代码出现具体工具 ID，边界已破" }
```

**注意**：`ContextBuilder` 里会出现 `Clipboard`、`Screenshot` 这类词，那是**输入来源**，不是工具，允许。

### 3.2 插件允许与禁止的依赖

| 允许 | 禁止 |
|---|---|
| `System.Text.Json` | `Avalonia.*` |
| `SkiaSharp` / `Svg.Skia` | `Sparrow.Core` |
| `Microsoft.Extensions.Logging.Abstractions` | `Sparrow.App` |
| 第三方纯计算库 | 其他插件 |
| | `Microsoft.Data.Sqlite`（存储归宿主） |

判断标准：**会不会产生副作用**。纯计算可以直连，副作用必须走 `IToolHost`。

### 3.3 SDK 打包时的坑

插件项目必须这样引用 SDK，否则产物里会拷一份 `Sparrow.Plugin.Sdk.dll`，导致 ALC 加载后 `ITool` 类型不匹配（见 [`plugin-model.md`](plugin-model.md#七alc-隔离的五条铁律)）：

```xml
<ProjectReference Include="..\..\src\Sparrow.Plugin.Sdk\Sparrow.Plugin.Sdk.csproj">
  <Private>false</Private>
  <ExcludeAssets>runtime</ExcludeAssets>
</ProjectReference>
```

---

## 四、工具契约（SDK 里的真实形状）

```csharp
// Sparrow.Plugin.Sdk/ITool.cs
public interface ITool
{
    ToolDescriptor Descriptor { get; }
    Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken ct);
}
```

**只有两个成员。** 元数据是静态属性（面板搜索不需要加载程序集执行），执行是唯一的副作用入口。

```csharp
public sealed record ToolDescriptor
{
    public required string Id { get; init; }              // sparrow.json.format
    public required string Name { get; init; }            // JSON 格式化
    public required string Category { get; init; }        // json
    public string Description { get; init; } = "";
    public string[] Keywords { get; init; } = [];
    public InputShape Accepts { get; init; }
    public OutputShape Produces { get; init; }
    public PlatformCapability Requires { get; init; }
    public IReadOnlyList<ToolParameter> Parameters { get; init; } = [];
    public int Order { get; init; }
}
```

```csharp
public sealed record ToolResult
{
    public required bool Success { get; init; }
    public IToolPayload? Payload { get; init; }           // 非空即成功
    public ToolError? Error { get; init; }
    public IReadOnlyList<ToolAction> Actions { get; init; } = [];
    public TimeSpan Duration { get; init; }
}
```

### 为什么 `ITool` 只有一个执行方法

| 常见设计 | 问题 |
|---|---|
| `InitializeAsync()` | 让「元数据可见」和「资源就绪」耦合 → 面板必须先加载程序集才能搜索 → 冷启动变慢 |
| `GetParameters()` | 参数表是静态的，放 `Descriptor` 即可 |
| `Dispose()` / `IAsyncDisposable` | 工具实例应无状态；有状态资源（模型、连接）放独立进程或 `IToolHost` |

**工具实例必须无状态、可并发。** 这是让调度器简单的唯一前提，也是让插件能随时卸载的前提。

---

## 五、版本钉死

`Directory.Packages.props` 是唯一版本来源。**以下四个版本禁止「顺手升级」。**

| 包 | 钉死版本 | 为什么不能升 |
|---|---|---|
| `Avalonia` / `Avalonia.Desktop` / `Avalonia.Themes.Fluent` | **12.1.6** | 核心与扩展包版本不同步，见 5.2 |
| `SkiaSharp` | **3.119.4** | 由 `Avalonia.Skia 12.1.6` 决定 |
| `Svg.Skia` | **5.1.1** | 5.2.0 起依赖 SkiaSharp 4.148.0，与 Avalonia 冲突 |
| `Avalonia.AvaloniaEdit` | **12.0.0** | 包名不是 `AvaloniaEdit`（那个停在 0.10.12） |

### 5.1 依赖冲突的实证

```text
Avalonia.Skia 12.1.6        → SkiaSharp 3.119.4, HarfBuzzSharp 8.3.1.3
Svg.Skia 5.2.3（最新）      → SkiaSharp 4.148.0,  HarfBuzzSharp 14.2.0   ← 冲突
Svg.Skia 5.1.1              → SkiaSharp 3.119.2,  HarfBuzzSharp 8.3.1.3   ← 兼容
```

SkiaSharp 3.x → 4.x 是 ABI 破坏性升级。NuGet 会把版本统一到 4.148.0，导致 `Avalonia.Skia` 在 4.x 二进制上运行，**编译通过、首次渲染崩溃**。

### 5.2 扩展包版本矩阵

| 包 | 可用最高版 | 处理 |
|---|---|---|
| `Avalonia.Controls.DataGrid` | 12.1.2 | 用到时单独钉 12.1.2 |
| `Avalonia.Controls.ItemsRepeater` | 12.0.0 | 用到时单独钉 12.0.0 |
| `Avalonia.Diagnostics`（DevTools） | 无 12.x | **接入方式需按 Avalonia 12 文档重查**，不要沿用 11.x 的 `AttachDevTools()` |

`Directory.Packages.props` 因此**不能**用 `<AvaloniaVersion>` 一个变量控制所有 Avalonia 包。

### 5.3 插件侧的版本策略

插件**锁自己的依赖版本**，与宿主无关（这正是 ALC 隔离的价值）：

```text
json-format 插件     → System.Text.Json（BCL，跟随宿主 runtime）
svg 插件             → SkiaSharp 3.119.4（必须与宿主一致，因为共享 native）
ocr 插件             → 独立进程，不受此约束
```

**例外**：SkiaSharp 是 native 库，同一进程内只能存在一份。所以 8 个插件里如果是 8 份 SkiaSharp 3.119.4，运行时仍是同一份 native 库。**这要求所有用 SkiaSharp 的插件必须锁同一个版本**，由 `Directory.Packages.props` 统一强制。

---

## 六、OCR 为什么独立进程

| 事实 | 数字 |
|---|---|
| `Sdcb.PaddleInference.runtime.win64.mkl` | 77.7 MB |
| `OpenCvSharp4.runtime.win` | 38.0 MB |
| PaddleOCR 原生 RID 覆盖 | win-x64 / linux-x64 / osx-x64，**无 arm64** |

三条后果：

1. **主包不可能 < 60MB** → OCR 组件首次使用时下载，不随主包分发。
2. **常驻内存不可能 < 150MB** → 模型不能常驻主进程。
3. **ARM 平台无解** → 明确「OCR 支持 Windows x64 / Linux x64 / macOS x64；ARM 走系统 OCR 兜底或置灰」。

```text
Sparrow.App (主进程)  ──stdio(JSON 长度前缀)──▶  Sparrow.Ocr.Host (子进程)
   │                                              ├─ 加载 Paddle 模型
   └─ Ocr 插件（普通插件）                          ├─ 识别
      只负责：截图的区域 → 传给子进程 → 收文本      └─ 空闲 60s 自动退出
```

**注意**：OCR **插件**本身仍是普通插件（B 档），它只是把重活委托给子进程。这样「每个工具都是插件」不破例。

---

## 七、错误与取消约定

```text
所有 ExecuteAsync 必须：
  1. 接受 CancellationToken 并向下传递
  2. 在超时内结束（文本类 10s / 描摹 120s / OCR 120s），否则被调度器取消
  3. 抛出的托管异常由调度器捕获，转成 ToolError，不穿透到 UI 线程
```

调度器的隔离边界：

```csharp
try   { return await tool.ExecuteAsync(ctx, cts.Token); }
catch (OperationCanceledException) { return ToolResult.Cancelled(); }
catch (Exception ex)               { return ToolResult.Failed(ex.Message, ex.ToString()); }
```

**插件不得自己弹窗、不得自己写日志文件、不得读设置文件。** 只能返回结果。

---

## 八、决策记录（ADR）

| # | 决策 | 理由 | 何时可推翻 |
|---|---|---|---|
| ADR-001 | 项目按「插件」拆，不按「层次」拆 | 层次拆分带来跨项目引用成本，插件拆分带来独立删除/分发能力 | 不推翻 |
| ADR-002 | **每个工具都是独立插件程序集**（ALC 隔离） | 集合能长期维持小，靠边界不靠自律；能砍、能禁用、能独立演进 | 不推翻 |
| ADR-003 | `ToolResult` 用 `IToolPayload` 而非可空字段 | 可空字段让「发送到其他工具」退化成 null 检查，且无法表达空结果 | 不推翻 |
| ADR-004 | SkiaSharp 钉 3.119.4 | Avalonia 12.1.6 的 ABI 要求；4.x 会运行时崩 | Avalonia 官方升到 4.x 时同步升 |
| ADR-005 | OCR 走独立进程 | 78MB+38MB 原生、无 arm64、模型吃内存 | 换成轻量引擎时 |
| ADR-006 | 不做插件市场、不做插件沙箱 | ALC 不是安全边界；插件必须可信 | 决定支持第三方分发时，需子进程 + 受限 IPC |
| ADR-007 | 内置工具也走插件机制，无双轨 | 双轨必然导致「内置能做的插件做不了」，边界一破就回单体 | 不推翻 |
| ADR-008 | 插件禁用时真卸载（ALC 回收） | 否则「禁用」只是隐藏，内存仍占着 | 不推翻 |

---

## 九、日志与数据位置

```text
%LOCALAPPDATA%\Sparrow\                  （Windows）
~/Library/Application Support/Sparrow/   （macOS）
~/.local/share/Sparrow/                  （Linux）
├─ settings.json        普通设置（含 schemaVersion、disabledPlugins）
├─ history.db           SQLite：历史 / 固定 / 会话
├─ secrets.bin          加密密钥（DPAPI / Keychain / libsecret）
├─ results/{id}.svg     大结果文件（超 256KB 才落盘）
├─ plugins/             用户放入的插件（与安装目录的 plugins 合并扫描）
└─ logs/                滚动日志，保留 7 天
```

**插件数据隔离**：每个插件的设置存 `settings.json` 的 `plugins.{pluginId}` 节点下，不单独占文件。

**日志红线**：禁止记录完整剪贴板内容、密钥、AI 请求原文。只记 `pluginId` / `toolId` / 耗时 / 结果大小 / 错误类型。
