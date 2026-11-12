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
   └─ Sparrow.Ocr.Worker.exe     ← 子进程，将来装 Paddle
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

### 5.1 SDK 包含什么（全部且仅有）

```text
Sparrow.Plugin.Sdk
├─ ITool.cs                工具的唯一实现接口
├─ IToolHost.cs            宿主注入的受限服务
├─ ToolDescriptor.cs       元数据
├─ ToolParameter.cs        参数声明
├─ ToolContext.cs          输入
├─ Inputs/                 TextInput, ImageInput, FileSetInput, EmptyInput
├─ ToolResult.cs           输出
├─ Payloads/               TextPayload, SvgPayload, ImagePayload, FileSetPayload, TablePayload
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
    string PluginDirectory { get; }           // 插件自己的目录，用于定位随包资源
}
```

进度上报不在这里，而在 `ToolContext.Progress`——因为它是「这一次执行」的状态，不是插件的长期能力。

**`ToolContext.Host` 是注入入口。** 放在 Context 而不是 `ITool.Initialize`，是为了守住
「`ITool` 只有两个成员、实现必须无状态」这条约束。插件每次执行都能拿到 Host，
但不得把它缓存到字段里（那会让卸载后的对象继续存活，违反铁律 4）。

`PluginDirectory` 是「进程插件」（OCR）唯一需要宿主额外提供的东西：
它得知道自己的 worker exe 和模型在哪。插件不得用它读写用户文件。

**没有文件系统、没有剪贴板、没有数据库、没有窗口。** 需要产出文件时，返回 `FileSetPayload`，由宿主决定怎么落盘。

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

## 十二、插件清单规范（给将来的自己看）

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

## 十三、验收：插件边界是否成立

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
