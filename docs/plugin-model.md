# 插件模型（Plugin Model）

> **每个工具都是一个插件。** 这是 Sparrow 的架构主张，不是可选项。

---

## 一、一句话定义

```text
插件 = 一个目录 + 一份清单 + 一个程序集 + 至少一个工具
```

工具不能「内置」在宿主里。**没有内置工具，也没有插件工具——只有插件。**

这不是洁癖。如果允许「内置工具」这条捷径存在，半年内必然出现：内置工具能拿到宿主内部 API、插件拿不到，于是「需要某个能力」的解法永远是「改成内置的」。插件边界一破，集合就退化成单体。

---

## 二、为什么每个工具都要是插件

| 收益 | 具体表现 |
|---|---|
| **能砍** | 删掉一个工具 = 删一个目录。不用改宿主、不用改注册表、不会留下死代码 |
| **能禁用** | 用户不用的工具彻底不加载，不占内存、不进搜索结果 |
| **能独立演进** | 一个工具的依赖升级（比如 Svg.Skia 5.1.1 → 未来版本）不影响其他工具 |
| **能验证边界** | 如果某个工具「必须改宿主才能做」，说明契约有漏——这是提前发现设计问题的探针 |
| **压迫接口设计** | 插件只能看到 `Sparrow.Plugin.Sdk`，宿主内部结构无法泄漏 |

对应到初衷里的「小而行」：**集合能长期维持小，靠的不是自律，是边界。**

---

## 三、三个档位：为什么选程序集级

| | A 逻辑插件 | **B 程序集插件** | C 进程插件 |
|---|---|---|---|
| 形态 | 同程序集，DI 注册 | 独立 dll + ALC 隔离 | 独立 exe + IPC |
| 能否独立分发 | ❌ | ✅ | ✅ |
| 能否禁用后不加载 | ❌ | ✅ | ✅ |
| 依赖隔离 | ❌ 版本冲突 | ✅ 每插件一套依赖 | ✅ |
| 崩溃隔离 | ❌ | ⚠️ 托管异常可捕获，原生崩溃带走宿主 | ✅ |
| 加载延迟 | 0 | ~10–50ms/插件 | ~100–500ms |
| 数据传递成本 | 0 | 0 | 高（图片要序列化过管道） |
| 适用 | 不是插件 | **默认所有工具** | 唯一例外：OCR |

**结论：默认 B；OCR 因为原生库体积（Paddle 78MB + OpenCV 38MB）和无 arm64 支持，额外套一层 C（子进程）。**

**M1 的实现形态**：`plugins/sparrow.plugin.ocr/` 里是「B 档薄壳 + C 档 worker」：

```text
plugins/sparrow.plugin.ocr/
├─ plugin.json
├─ Sparrow.Plugin.Ocr.dll        ← 实现 ITool，宿主只看到这个
└─ worker/
   └─ Sparrow.Ocr.Worker.exe     ← 子进程，内置 PP-OCRv5
```

宿主侧代码完全不知道子进程存在。插件通过 `IToolHost.PluginDirectory`
定位 worker，用 `stdin/stdout` 传图与文本。

M1 已经接入真实模型：**PP-OCRv5（本地模型，Apache 2.0）**，
按 `language` 参数切换 12 组识别模型（中英日、俄语/乌克兰、韩语、阿拉伯语、泰语、希腊语、
印地语、泰米尔语、泰卢固语、拉丁语系），检测与方向分类共用。
单张截图约 2.1–2.7 秒（含模型初始化）。
worker 目录约 500 MB（PaddleInference + MKL + OpenCV + 模型）。
换成 PaddleOCR-VL 或别的模型时，宿主与本协议都不用改。

C 档不推广的原因：进程间传图片的成本比想象中高（4K 截图 ≈ 25MB，序列化 + 拷贝 + 反序列化 ≈ 100ms+），而大部分工具是纯计算，进程隔离的收益为负。

---

## 四、插件形态

```text
plugins/
└─ sparrow.plugin.json-format/          ← 目录名 = 插件 ID
   ├─ plugin.json                        ← 清单
   ├─ Sparrow.Plugin.Json.Format.dll     ← 入口程序集
   └─ (该插件自己的依赖，如 Newtonsoft.Json.dll)
```

**目录名必须等于 `plugin.json` 里的 `id`。** 这是发现插件的唯一依据，不做模糊匹配。

### 4.1 清单（plugin.json）

```json
{
  "id": "sparrow.plugin.json-format",
  "name": "JSON 格式化",
  "version": "1.0.0",
  "sdkVersion": "1.0",
  "entryAssembly": "Sparrow.Plugin.Json.Format.dll",
  "author": "sparrow",
  "description": "格式化、压缩、校验 JSON",
  "capabilities": [],
  "tools": ["sparrow.json.format"]
}
```

| 字段 | 必填 | 说明 |
|---|:--:|---|
| `id` | ✅ | 插件唯一标识，稳定不变，必须等于目录名 |
| `name` | ✅ | 面板显示名 |
| `version` | ✅ | 语义化版本 |
| `sdkVersion` | ✅ | 编译时依赖的 SDK 版本，形如 `1.0` |
| `entryAssembly` | ✅ | 入口 dll 文件名 |
| `tools` | ✅ | 本插件提供的工具 ID 列表，**至少一个** |
| `capabilities` | ✅ | 声明的平台能力（空数组表示纯计算） |
| `author` / `description` | ⬜ | 展示用 |

### 4.2 一个插件几个工具

**默认 1 个。** 允许声明多个，仅限「共享不可分割资源」的情况：

```text
✅ OCR：region + image 共用同一个 Paddle 模型，拆开要加载两遍
✅ JSON：format + toCode + query 共用同一套解析器（可选合并）
❌ 「文本工具合集」：6 个互不相关的工具塞一个插件 —— 这就是回到了单体
```

判断标准：**拆开后是否要重复加载同一份重资源**。不是，就必须拆。

---

## 五、SDK 契约

插件只能引用一个包：

```text
Sparrow.Plugin.Sdk        ← 版本化，有向后兼容承诺
```

**插件禁止引用 `Sparrow.Core`、`Sparrow.App`、任何其他插件。**

例外：插件可以包含并引用**自己的私有子进程项目**（如 OCR 的 `worker/`），用于把原生依赖隔离到独立进程。这个子项目不得引用宿主或其他插件，也不得把宿主类型带进插件输出。

### 5.1 SDK 包含什么（全部且仅有）

```text
Sparrow.Plugin.Sdk
├─ ITool.cs                工具的唯一实现接口
├─ IToolHost.cs            宿主注入的受限服务
├─ ToolDescriptor.cs       元数据
├─ ToolParameter.cs        参数声明
├─ ToolContext.cs          输入
├─ Inputs/                 TextInput, ImageInput, FileSetInput, EmptyInput
├─ IFileAccess.cs          本次输入对应的只读文件访问（仅在 Files 输入时非空）
├─ ToolResult.cs           输出
├─ Payloads/               TextPayload, OcrResultPayload, SvgPayload, ImagePayload, FileSetPayload, TablePayload
├─ Ai.cs                   IAiClient, AiRequest, AiResponse（密钥与端点由宿主管理）
├─ ToolAction.cs  ToolError.cs  ToolProgress.cs
└─ Enums/                  InputShape, OutputShape, ToolSource, PlatformCapability
```

### 5.2 SDK 不包含什么（刻意的）

| 不提供 | 原因 |
|---|---|
| `IHistoryStore` | 历史由宿主记录，插件不参与 |
| `ISettingsStore` | 插件用 `IToolHost.Settings`，按 toolId 隔离 |
| `IClipboardService` | 输入已由 `ToolContext` 提供，插件不需要读剪贴板 |
| `ISecretStore` | 密钥不进插件 |
| 任何 Avalonia 类型 | 插件不拥有 UI |
| `ILogger` 之外的日志设施 | 用 `IToolHost.Logger` |

```csharp
// 插件能用的全部外部能力
public interface IToolHost
{
    ILogger Logger { get; }
    IToolSettings Settings { get; }           // 按 toolId 隔离的键值存储
    IHttpClientAccess Http { get; }           // 受策略控制，可被禁用
    IAiClient Ai { get; }                     // 模型能力；未配置时 IsAvailable=false
    string PluginDirectory { get; }           // 插件自己的目录，用于定位随包资源
    IClipboardHistory? ClipboardHistory { get; } // 剪贴板历史；用户关闭记录或测试场景为 null
    IWindowOverlay? Windows { get; }          // 通用窗口覆盖：拾取 / 图钉；无 UI 场景为 null
}
```

**`Windows` 只提供通用覆盖能力，不暴露宿主内部窗口。** 拾取遮罩、图钉指示都由宿主创建，
插件只描述「要拾取窗口 / 要在某句柄上显示图钉」；任何插件都能用同一套能力，
不是为某个工具定制的后门。

### 5.3 结果按载荷渲染，不能谎报失败

`ToolResult.Payload` 的类型就是「这张卡片长什么样」的声明：

| 载荷 | 宿主渲染 |
|---|---|
| `TextPayload` / `OcrResultPayload` | 可选中文本（OCR 另带行框，供截图叠加层点行复制） |
| `SvgPayload` | 安全清洗 + 渲染预览，可切换源码 |
| `ImagePayload` | 位图预览 + 复制 / 保存 |
| `TablePayload` | 等宽对齐表格（表头 + 分隔线） |
| `FileSetPayload` | 文件清单（数量 + 路径） |

宿主必须覆盖 SDK 里公开的每一种载荷。**不认识的载荷只能显示占位说明，不能当成执行失败**——
`Success = true` 就是成功，UI 没做专用渲染是宿主的问题，不是工具的问题。

**`Ai` 只暴露「发消息、拿回复」。** provider、Base URL、模型名、API Key 全部留在宿主：
插件不知道请求发往哪里，也不能读取密钥。需要视觉模型时传 `AiRequest.Image`，宿主负责编码成 data URL。

**`ClipboardHistory` 是唯一的剪贴板入口，且是历史、不是实时。** 监听 Win32 剪贴板、
落盘、图片转 PNG 全部由宿主完成（`ClipboardMonitor` + `ClipboardHistoryStore`），
插件只读整理好的条目；需要内容时文本在 `ClipboardHistoryEntry.Text`，
图片经 `LoadImage` 取回字节，插件仍然不碰文件系统。

进度上报不在这里，而在 `ToolContext.Progress`——因为它是「这一次执行」的状态，不是插件的长期能力。

**文件读取同理，也在 `ToolContext.Files`（`IFileAccess`）。** 输入是 `FileSetInput` 时宿主注入一个
只读访问器：只允许读取本次输入里出现的路径，越权返回 `null`。插件因此能处理用户拖入的文件，
但依然不能遍历磁盘、不能读用户的其他文件。批量识别、文件哈希这类工具都走这条通路。
小文件用 `ReadAllBytes`，大文件用 `OpenRead` 流式处理（哈希不会把整个文件读进内存）。
这是**契约层的授权**，不是安全沙箱——插件与宿主同进程，仍可直接 `File.ReadAllBytes`（见 10.1）。

**`ToolContext.Host` 是注入入口。** 放在 Context 而不是 `ITool.Initialize`，是为了守住
「`ITool` 只有两个成员、实现必须无状态」这条约束。插件每次执行都能拿到 Host，
但不得把它缓存到字段里（那会让卸载后的对象继续存活，违反铁律 4）。

`PluginDirectory` 是「进程插件」（OCR）唯一需要宿主额外提供的东西：
它得知道自己的 worker exe 和模型在哪。插件不得用它读写用户文件。

**没有文件系统、没有剪贴板、没有数据库、没有窗口。** 需要产出文件时，返回 `FileSetPayload`，由宿主决定怎么落盘；
需要读取用户拖入的文件时，用 `ToolContext.Files`（只读、按次授权）。

---

## 六、加载流程

```text
宿主启动
  ├ 1. 扫描 plugins/ 下所有目录
  ├ 2. 读 plugin.json，JSON 解析失败 → 跳过并记日志
  ├ 3. 校验：id == 目录名？tools 非空？sdkVersion 兼容？
  ├ 4. 校验：capabilities ⊆ 当前平台能力？（不满足只标记不可用，不阻止加载元数据）
  ├ 5. 读 settings 里的 disabledPlugins，命中则跳过
  ├ 6. 创建 PluginLoadContext（isCollectible: true）
  ├ 7. 加载 entryAssembly，找 ITool 实现
  ├ 8. 校验 Descriptor.Id 在 manifest 的 tools 列表里
  └ 9. 注册到 ToolRegistry（只注册元数据，执行时才调）
```

**关键：第 3–5 步只读清单，不加载程序集。** 这样面板能立刻显示所有工具名，程序集按需加载（`Assembly.Load` 延迟到首次执行）。

冷启动路径上不允许加载任何插件程序集——这是「冷启动 < 2.5s」的前提。

---

## 七、ALC 隔离的五条铁律

这是 .NET 插件最容易踩死的地方。**违反任何一条，插件会静默不加载，且没有错误提示。**

### 铁律 1：SDK 程序集必须由宿主解析

```csharp
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string entryPath) : base(isCollectible: true)
        => _resolver = new AssemblyDependencyResolver(entryPath);

    protected override Assembly? Load(AssemblyName name)
    {
        // ★ 绝对不能从插件目录加载 SDK，否则 ITool 会变成两个不同的类型
        if (name.Name!.StartsWith("Sparrow.Plugin.Sdk"))
            return null;   // 返回 null = 交给默认 ALC

        var path = _resolver.ResolveAssemblyToPath(name);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
```

**如果不这样做**：插件目录里如果有一份 `Sparrow.Plugin.Sdk.dll`（构建产物必然会拷一份），ALC 会加载它，于是：

```text
宿主看到的 ITool  = ALC_Default.Sparrow.Plugin.Sdk.ITool
插件实现的 ITool  = ALC_Plugin0.Sparrow.Plugin.Sdk.ITool
→ tool is ITool == false
→ 插件「加载成功」但一个工具都没有
```

这是最隐蔽的错误：无异常、无日志、面板里就是没有。

**配套措施**：插件项目里 `Sparrow.Plugin.Sdk` 必须设 `PrivateAssets="all"` 或 `ExcludeAssets="runtime"`，产物不拷 SDK dll。同时加载器在 `Load()` 里做兜底（上面的代码）。

### 铁律 2：每个插件一个 LoadContext

```text
plugins/sparrow.plugin.json-format/  → PluginLoadContext #1
plugins/sparrow.plugin.dev-base64/   → PluginLoadContext #2
```

不要共用一个 ALC。共用会导致 A 插件引用的 `Newtonsoft 13.0` 和 B 插件引用的 `Newtonsoft 12.0` 互相污染。

### 铁律 3：ALC 必须 `isCollectible: true`

否则无法卸载。**卸载能力是「禁用插件」的前提**——禁用后要真释放内存，而不是只从列表里隐藏。

### 铁律 4：插件内不能有存活引用

ALC 卸载的条件是「该上下文中没有任何存活的引用」。以下任何一个都会导致泄漏：

```text
✗ 静态事件订阅了宿主的事件
✗ Timer / Thread 仍在运行
✗ 宿主持有了插件的对象引用（比如把 ITool 实例存在字段里）
✗ 插件注册了 AppDomain.ProcessExit
```

**宿主的做法**：`ToolRegistry` 只存 `WeakReference<ITool>`，卸载时置空；卸载前调 `plugin.Dispose()`，并断言 ALC 可回收（`GC.Collect()` 后 `weakRef.IsAlive == false`，不成立就记告警）。

### 铁律 5：插件不注册任何全局钩子

热键、托盘、文件关联、协议注册——全部由宿主独占。插件声明需求，宿主实现。

---

## 八、生命周期

```text
        ┌──────────┐
        │ Discovered│  只读清单，未加载程序集
        └────┬─────┘
             │ 用户首次执行 / 预加载
        ┌────▼─────┐
        │  Loaded  │  ALC 创建，程序集加载，实例创建
        └────┬─────┘
             │
        ┌────▼─────┐     禁用       ┌──────────┐
        │  Active  │ ─────────────▶ │ Disabled │
        └────┬─────┘                └────┬─────┘
             │ 连续失败 3 次              │ 启用
        ┌────▼─────┐                    │
        │  Faulted │ ◀──────────────────┘
        └────┬─────┘
             │ 卸载
        ┌────▼─────┐
        │ Unloaded │  ALC 回收
        └──────────┘
```

| 状态 | 面板表现 | 能否执行 |
|---|---|---|
| Discovered | 正常显示 | 能（首次执行时加载） |
| Loaded / Active | 正常显示 | 能 |
| Disabled | 不显示（设置页可见） | 不能 |
| Faulted | 灰显 + 「插件异常」 | 不能，除非手动重试 |
| 能力不满足 | 灰显 + 原因文案 | 不能 |

**实现状态（M1）**：

| 状态 | 状态 |
|---|---|
| Discovered → Loaded → Active | ✅ 已实现（懒加载，首次搜索触发） |
| Disabled | ✅ 已实现（启动时读 `settings.json` 的 `disabledPlugins`） |
| Unloaded | ✅ 已实现（`ToolCatalog.UnloadPlugin`，GC 后断言 ALC 回收） |
| Faulted（连续失败 3 次） | ⬜ 未实现 |

卸载的回收断言在 `tests/Sparrow.Tests/PluginLoadingTests.cs` 的 `卸载插件_ALC能被回收`：
`Unload()` 后多轮 `GC.Collect`，弱引用必须变成 `IsAlive == false`。
如果这个测试红了，说明宿主某处还持有插件对象——这是铁律 4 唯一的自动化防线。

---

## 九、版本兼容

```text
SDK 版本规则：
  1.x  →  1.y  向后兼容（插件不用改）
  1.x  →  2.0  不兼容（插件需重新编译）
```

宿主校验：

```text
plugin.sdkVersion 的 major  ==  host.sdkVersion 的 major   → 兼容
否则 → 标记「需要更新插件」，不加载
```

**为什么不做自动适配**：契约是编译期绑定的，运行时 shim 只会把不兼容问题推迟到更难查的地方（`MissingMethodException` 出现在用户机器上）。

`Sparrow.Plugin.Sdk` 的公开 API 一旦发布，**只能加，不能改签名、不能删**。这是插件生态的底线，也是为什么 M0 阶段要一次把契约想清楚。

| SDK 版本 | 新增（均为向后兼容） |
|---|---|
| 1.2 | `IToolHost.Windows` / `IWindowOverlay`：窗口拾取 + 图钉 |
| 1.3 | `ToolContext.Files` / `IFileAccess`：本次输入的只读文件访问 |
| 1.4 | `IFileAccess.OpenRead`：流式读取，供哈希等大文件工具使用 |

---

## 十、故障处理（诚实版）

| 故障 | 宿主表现 |
|---|---|
| 清单 JSON 语法错 | 跳过该插件，日志 Warn，设置页显示「清单无效」 |
| 入口 dll 不存在 | 跳过，日志 Error，设置页显示「文件缺失」 |
| 找不到 `ITool` 实现 | 提示「未找到工具实现」——多半是铁律 1 违反了 |
| `ExecuteAsync` 抛托管异常 | 捕获 → `ToolResult.Failed` → 失败计数 +1；连续 3 次 → Faulted |
| `ExecuteAsync` 超时 | 取消 → 提示「执行超时」 |
| 插件原生崩溃（AccessViolation） | **宿主一起崩** —— 无法隔离，见下 |

### 10.1 插件化不提供安全沙箱

必须写清楚，否则会误判：

```text
ALC 是「加载隔离」，不是「安全隔离」。
插件与宿主同进程、同权限，可以用反射访问任何托管对象、
可以 P/Invoke 任意系统调用、可以读写用户文件。
```

**因此：**

1. **插件必须可信**。M0–M5 只加载随包分发或用户手动放入的插件，不做「下载即安装」。
2. **不做插件市场**（已在 `scope.md` 标为永久不做）。
3. 唯一需要真隔离的（OCR）走独立进程，因为它同时具备「原生库重」和「崩溃会带走宿主」两个特征。

如果将来要做第三方插件生态，需要的是**子进程 + 受限 IPC + 权限声明**，不是加强 ALC。那时是一次架构升级，不是改配置。

---

## 十一、内置工具不是例外

M0 的 3 个工具同样以插件形式存在，随安装包分发到 `plugins/` 目录：

```text
安装目录/
├─ Sparrow.App.exe
├─ Sparrow.Plugin.Sdk.dll
└─ plugins/
   ├─ sparrow.plugin.clipboard-history/
   ├─ sparrow.plugin.json-format/
   └─ sparrow.plugin.ocr-region/
```

**没有任何代码路径能区分「内置插件」和「外部插件」。** 唯一的差别是「谁把它放进 plugins 目录」。

这条如果不遵守，插件边界会在第一次「这个功能比较特殊，我直接写进宿主吧」时破掉。

---

## 十二、宿主能力边界

「工具都是插件」有一条配套边界。不写清楚，就会出现「这个功能特殊，写进宿主吧」的滑坡：

| 归属 | 内容 | 例子 |
|---|---|---|
| **宿主：输入采集** | 把系统的原始信号统一成同一个 `IToolInput` | 剪贴板文本/图片、截图、选区、拖入文件、手输 |
| **宿主：统一结果动作** | 所有工具共用的结果出口 | 复制 / 保存（图片落盘、文本另存为）/ 固定 / 发送给其他工具 / 打开所在位置 |
| **宿主：运行时环境** | 工具跑起来所需的外壳 | 全局热键、命令面板、工作区窗口、托盘、设置页、插件加载与禁用 |
| **插件：工具本身** | 一次调用完成一件事 | JSON 格式化、Base64、OCR、翻译、图片转换、窗口置顶 |

### 判定流程（新功能先过这三问）

1. 它产出的是**输入 / 结果 / 运行时**，还是一次**工具调用**？
2. 它是否依赖宿主独有的东西（弹窗、写盘、写剪贴板、全局鼠标监听、密钥管理）？
3. 如果依赖，**是不是所有插件都可能需要同一个能力**？
   - 是 → 补**通用** SDK 能力（写进契约），功能仍然做成插件
   - 否 → 它不是宿主能力，回到插件里用允许的手段实现

### 先例对照

| 功能 | 归属 | 理由 |
|---|---|---|
| 截图、滚动拼接 | 宿主 | 产生输入；插件只拿到 `ImageInput` |
| OCR | 插件 | 消费输入；宿主不知道 worker 进程存在 |
| 保存图片 | 宿主结果动作 | 落盘是宿主的事，插件只返回 `ImagePayload` |
| 表格 / 文件列表展示 | 宿主结果展示 | 插件只返回 `TablePayload` / `FileSetPayload`；怎么排版是宿主的事 |
| 剪贴板历史 | 宿主监听/落盘，插件只读 | 隐私开关在宿主；插件不实时读剪贴板 |
| AI / HTTP | 宿主 | 密钥、端点、联网策略由宿主统一管 |
| 文本导出 TXT/MD/Word | 宿主结果动作（Save 的扩展） | 不是工具，是结果出口 |
| 窗口置顶 | **插件 + 通用宿主覆盖能力** | 拾取遮罩与图钉由通用 `IWindowOverlay` 提供；置顶逻辑在插件 |
| 文件预览弹窗 | 插件 + 契约缺口 | 预览窗必须先成为通用 SDK 能力，不能借机做成内置工具 |
| 读取用户拖入的文件 | 宿主：输入采集（`ToolContext.Files`） | 按次授权、只读、只允许本次输入的路径；文件类工具仍是插件 |
| 批量处理 | 插件 | 宿主只提供多文件输入（`FileSetInput`），批量逻辑与循环在插件里；宿主编排工具串 = 工作流，踩「不是平台」红线 |

### 三条红线

1. **不为单个工具加宿主专用接口。** 专用接口等于变相内置。
2. **插件不得依赖宿主内部实现**（内部类型、静态状态、私有文件）。可以 P/Invoke 系统 API——插件必须可信（ADR-006），但系统能力不是宿主能力。
3. **遇到缺口先补契约，不补内置。** 补完契约后，功能仍以插件目录形态存在，随包分发也走同一条加载路径。

---

## 十三、插件清单规范（给将来的自己看）

```text
命名：
  插件 ID   sparrow.plugin.<分类>-<动作>      例：sparrow.plugin.json-format
  目录名    与插件 ID 完全一致
  工具 ID   sparrow.<分类>.<动作>             例：sparrow.json.format
  程序集    Sparrow.Plugin.<分类>.<动作>.dll
  命名空间  Sparrow.Plugin.<分类>.<动作>

多工具插件（共享不可分割的重资源，见 4.2）：
  插件 ID   sparrow.plugin.<分类>             例：sparrow.plugin.ocr
  程序集    Sparrow.Plugin.<分类>.dll
  命名空间  Sparrow.Plugin.<分类>
  工具 ID   各自 sparrow.<分类>.<动作>         例：sparrow.ocr.image / sparrow.ocr.region

文件：
  plugin.json          必须 UTF-8 无 BOM
  <entry>.dll          必须与清单 entryAssembly 一致
  依赖 dll             与 entry 同目录（扁平放置，不做子目录）

约束：
  单插件文件总量 < 20MB（OCR 走独立进程，不受此限）
  入口 dll < 2MB
  清单解析 < 5ms（决定冷启动能扫多少插件）
```

---

## 十四、验收：插件边界是否成立

| # | 问题 | 期望答案 |
|---|---|---|
| 1 | 删掉 `plugins/sparrow.plugin.json-format/` 后，宿主还能启动吗？ | 能，且面板里没有该工具，无报错 |
| 2 | 插件能 `new HttpClient()` 吗？ | 能，但会被 lint 拦（应该用 `IToolHost.Http`） |
| 3 | 插件能引用 Avalonia 吗？ | 技术上能，契约上禁止，CI 检查 csproj |
| 4 | 加一个新工具，要改宿主代码吗？ | 不能 |
| 5 | 插件执行崩溃，宿主会崩吗？ | 托管异常不会；原生崩溃会（已知限制） |
| 6 | 禁用插件后，内存释放了吗？ | 是，ALC 已回收 |
| 7 | 插件 A 和插件 B 引用同一个库的不同版本，会冲突吗？ | 不会，各自 ALC |

第 1 条是**最重要的自检**：如果删掉一个插件会让宿主起不来，说明边界已经破了。

第 4 条曾经在截图入口破过：`App` 里写死了 `Descriptor.Id == "sparrow.ocr.image"`，
加第二个图像工具就得改宿主。现在改为按 `Descriptor.Accepts` 查表
（`ToolRegistry.FindAccepting`），并由 `tests/Sparrow.Tests/ToolRegistryTests.cs` 守住。
截图动作栏目前只取第一个匹配项；等真的出现第二个图像工具，再把它扩成动态按钮 + 分组 + 溢出菜单。
