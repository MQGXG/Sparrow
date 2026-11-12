# 上下文模型（Context Model）

> 工具集合能成立的前提：**所有入口交出同一种输入。**

如果每个工具自己决定「从哪拿数据」，那么加第 N 个工具就要重新处理一遍剪贴板、截图、拖拽。这份文档定义唯一的输入形状。

---

## 一、核心思想

```text
六种入口  ──▶  ContextBuilder  ──▶  一个 ToolContext  ──▶  所有工具
```

工具**永远不知道**数据来自剪贴板还是截图。它只看到 `ToolContext.Input`，以及一个用来做提示的 `Source` 标签。

---

## 二、输入来源

```csharp
// Sparrow.Plugin.Sdk/Enums/ToolSource.cs
public enum ToolSource
{
    Manual,       // 用户手输
    Clipboard,    // 剪贴板
    Selection,    // 前台应用选区（UI Automation）
    Screenshot,   // 截图
    Dropped,      // 拖入文件/图片
    PreviousTool  // 上一个工具的输出（接力）
}
```

`Source` **只用于展示和日志**（例如「来自剪贴板」），不得用于分支逻辑。分支只看 `Input` 的形态。

---

## 三、输入载体

```csharp
// Sparrow.Plugin.Sdk/ToolContext.cs
public sealed record ToolContext
{
    public required IToolInput Input { get; init; }
    public ToolSource Source { get; init; }
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = ...;

    // 接力元数据（见第五节）
    public string? TraceId { get; init; }
    public int Depth { get; init; }

    public IProgress<ToolProgress>? Progress { get; init; }
}
```

```csharp
public interface IToolInput { }

public sealed record TextInput(string Text) : IToolInput;
public sealed record ImageInput(ImageRef Image) : IToolInput;
public sealed record FileSetInput(IReadOnlyList<string> Paths) : IToolInput;
public sealed record EmptyInput : IToolInput;
```

### 3.1 参数为什么是 `string`

`Dictionary<string, string>` 而不是 `object`：

- 参数只来自 UI 控件和设置文件，两者本质都是字符串；
- 需要类型时由工具自己 `int.Parse` / `Enum.Parse`，转换失败返回 `ToolError` 而不是抛异常；
- 序列化进历史库时不丢类型信息，也不会有 `JsonElement` 生命周期问题。

**接受这个取舍**：牺牲一点类型安全，换来「加参数不需要改契约」。参数数量控制在 5 个以内，靠 UI 约束而不是靠类型系统约束。

---

## 四、从入口到 ToolContext

```csharp
// App/Services/ContextBuilder.cs （简化）
public ToolContext FromClipboard()  => new() { Input = ReadClipboard(), Source = Clipboard };
public ToolContext FromSelection()  => new() { Input = new TextInput(uia.GetSelectedText()), Source = Selection };
public ToolContext FromScreenshot(ImageRef shot) => new() { Input = new ImageInput(shot), Source = Screenshot };
public ToolContext FromDropped(IDataObject data) => ...;
```

### 4.1 各来源的平台实现差异（这是真正的成本所在）

| 来源 | Windows | macOS | Linux |
|---|---|---|---|
| **剪贴板变更事件** | `AddClipboardFormatListener` + 隐藏消息窗口，**有事件** | **无事件**，只能轮询 `NSPasteboard.changeCount`（建议 300–500ms） | X11 有 selection 事件；Wayland 基本不可用 |
| **前台选区文本** | UI Automation（`TextPattern`），覆盖大部分原生控件 | 辅助功能授权 + `AXSelectedText`，需要用户手动开权限 | 基本放弃 |
| **截图** | `BitBlt` / `PrintWindow` / Win10+ `GraphicsCapture` | `CGWindowListCreateImage`，需屏幕录制授权 | X11 `XGetImage`；Wayland 需 portal |
| **拖入文件** | 统一 `IDataObject` | 统一 | 统一 |

**结论**：上下文模型的抽象是跨平台的，但**实现不是**。M0 只做 Windows，其他平台靠 `IPlatformCapabilities` 把不支持的工具置灰，而不是假装能跑。

### 4.2 能力声明

```csharp
[Flags]
public enum PlatformCapability
{
    None = 0,
    Clipboard = 1 << 0,
    ClipboardMonitor = 1 << 1,   // 有变更事件（macOS 为 false）
    Screenshot = 1 << 2,
    Selection = 1 << 3,
    GlobalHotkey = 1 << 4,
    Ocr = 1 << 5,
    SvgRender = 1 << 6,
    Network = 1 << 7
}
```

工具声明 `Requires`，注册表在加载时按当前平台过滤。**不满足能力的工具不出现在面板里**——比出现后报错好。

---

## 五、大对象处理

剪贴板里可能有 500MB 的视频，截图可能是 4K。**不能把这些塞进 ToolContext 反复传递。**

```csharp
// 超过阈值的内容只传引用
public sealed record ImageRef(string CacheKey, int Width, int Height, long ByteSize);
```

规则：

| 类型 | 阈值 | 处理 |
|---|---|---|
| 文本 | > 256 KB | 只传前 256KB + `IsTruncated = true`，UI 提示「内容过大，已截断」 |
| 图片 | 总是 | 传 `ImageRef`，实体在图床（临时目录 + LRU） |
| 文件 | 总是 | 只传路径列表，不读内容 |
| 结果 | > 256 KB | 落盘到 `results/{id}.ext`，`Payload` 里存路径 |

**历史库同理**：只存预览（前 2KB）+ 指针，不存全文。否则 `history.db` 会在一周内涨到几个 GB。

---

## 六、流转与环路保护

### 6.1 流转规则

「发送到其他工具」= 把当前 `Payload` 转成下一个工具的 `Input`，**且必须满足矩阵约束**：

```text
TextPayload   → TextInput     ✅
SvgPayload    → TextInput     ✅（SVG 源码是文本）
FileSetPayload→ FileSetInput  ✅
ImagePayload  → ImageInput    ✅
TablePayload  → TextInput     ✅（转 TSV）
```

不满足的组合（如 `SvgPayload → ImageInput`）在 UI 上直接不列为候选，不做隐式转换。

### 6.2 环路保护

```csharp
public sealed record ToolContext
{
    public string? TraceId { get; init; }   // 一次接力链共用一个
    public int Depth { get; init; }         // 每接力一次 +1
}
```

```text
Depth >= 3  → 「发送到其他工具」置灰，只留 复制 / 保存
```

同时历史里按 `TraceId` 聚合，一次链路的多个结果折叠成一组，而不是给用户刷出 5 条记录。

### 6.3 为什么不用「工作流」来表达

有人会问：既然能接力，为什么不画个流程？

因为**接力是即时的、一次性的、由人决策的**，而工作流是**预先定义的、可重复的、无人值守的**。后者需要编排器、变量、错误重试、调试器——那是另一个产品，且违反「一次热键闭环」。

Sparrow 只做「这一次」，不做「每次都这样做」。

---

## 七、上下文模型的验收

模型是否正确，用三个问题检验：

```text
1. 加一个只用 TextInput 的工具，需要改 ContextBuilder 吗？   → 不能改
2. 加一种新来源（比如「扫描二维码」），要改所有工具吗？        → 不能改
3. 工具能知道自己来自剪贴板还是截图并改行为吗？                → 不能
```

任何一条答错，说明抽象漏了。
