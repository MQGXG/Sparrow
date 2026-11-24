# 2026-11-24 Sparrow 作为 Agent 能力端点（方向设计）

> 状态：方向提案，未进入实现。
> 本文只定义「Sparrow 如何被外部 agent 使用」，**不改变** `scope.md` / `roadmap.md` 的现有范围。
> 若要转为主线，必须先显式修改那两份文档，本文不能绕过它们。

---

## 一、一句话

**Sparrow 不做 agent，只做本地能力端点。**

外部 agent 负责理解、决策和编排；Sparrow 负责提供**工具**和**桌面上下文**（屏幕、元素、剪贴板、文件）。

```text
外部 agent（大脑）  ──调用──▶  Sparrow（手眼）  ──返回──▶  结构化结果
```

---

## 二、为什么是「端点」，不是「agent」

| 角色 | 谁来做 |
|---|---|
| 理解意图、规划步骤 | 外部 agent |
| 决定调用哪个工具、传什么参数 | 外部 agent |
| 执行工具、返回结构化结果 | Sparrow |
| 采集屏幕 / 元素 / 剪贴板信息 | Sparrow |
| 动作回放（点击 / 输入） | 暂不做（见 S4） |

这个定位避开三条现有红线：

- **不是 AI 应用**：Sparrow 不要求配模型，agent 自带模型；
- **不是平台**：不做市场、不做托管、不做编排；
- **不做工作流 / 脚本运行时**：循环和编排都在外部。

---

## 三、与现有方向的边界

### 3.1 不冲突的部分

| 现有原则 | 如何保持 |
|---|---|
| 每个工具都是独立插件 | 对外暴露的仍是插件工具，不新增「内置工具」 |
| 六种入口统一成 `ToolContext` | agent 复用 `TextInput / ImageInput / ImageSetInput / FileSetInput`，**不新增输入形态** |
| 本地优先 | 不引入云服务，端点只绑定本机 |
| AI 可选 | agent 自带模型；Sparrow 的 `IAiClient` 不参与这条链路 |

### 3.2 需要明确的边界

- agent 是**第 7 个入口**，不是新输入类型；
- **只读优先**，动作能力单独评审（S4）；
- 对外暴露的是**精选工具**，不是全部工具。

### 3.3 仍然不做

- 内置 agent / 决策循环；
- 后台调度、监看、定时；
- 工作流编排、脚本运行时；
- 插件市场、账号、云同步。

---

## 四、核心原则

1. **契约优先于协议**：先定 `tool id + 输入 schema + 输出 JSON + 错误码`，再选传输方式。
2. **只读优先**：先提供信息（S1–S3），再讨论动作（S4）。
3. **能力归宿主，工具归插件**：对外端点属于宿主能力，不塞进某个插件。
4. **授权归宿主**：agent 只能请求，不能绕过确认。
5. **新鲜度显式**：所有屏幕/元素信息带 `snapshotId / capturedAt`，由 agent 判断是否过期。

---

## 五、能力分层

| 层 | 能力 | 例子 | 状态 |
|---|---|---|---|
| **S1** | 纯函数工具 | JSON 格式化、Base64、Hash、正则、图片转换/缩放、OCR（传字节）、翻译 | **先做** |
| **S2** | 宿主数据 | 剪贴板读/写、文件读取 | 需授权 |
| **S3** | 桌面信息 | `inspect(point)`、`resolve(key)`、截图、屏幕 OCR | 需用户确认 |
| **S4** | 动作 | `ui.invoke` / 点击 / 输入 | 另议，默认不做 |

对外只暴露 `ToolDescriptor.Requires` 满足、且用户已授权的工具。

---

## 六、信息契约（S3）

### 6.1 `inspect(point)`

**输入**

```json
{ "x": 1541, "y": 1070, "coordinateSpace": "physical-pixels" }
```

**输出**

```json
{
  "snapshotId": "20261124-100139-0x10802-1187",
  "capturedAt": "2026-11-24T10:01:39.971+08:00",
  "coordinateSpace": {
    "kind": "physical-pixels",
    "virtualDesktop": { "x": 0, "y": 0, "width": 3840, "height": 2160 },
    "dpi": { "awareness": "per-monitor-v2", "scale": 2.0 }
  },
  "target": {
    "hwnd": "0x10802",
    "processName": "msedge",
    "windowTitle": "dotnet/skills ... - Microsoft Edge",
    "className": "Chrome_WidgetWin_1",
    "bounds": { "x": 0, "y": 0, "width": 3840, "height": 2100 }
  },
  "element": {
    "role": "ListItem",
    "name": ".config",
    "automationId": null,
    "className": null,
    "bounds": { "x": 1282, "y": 1045, "width": 543, "height": 61 },
    "isEnabled": true,
    "isOffscreen": false,
    "patterns": ["Invoke", "SelectionItem"]
  },
  "path": [
    { "role": "ListItem", "name": ".config", "bounds": { "...": "..." } },
    { "role": "List", "name": "...", "bounds": { "...": "..." } },
    { "role": "Pane", "name": "...", "bounds": { "...": "..." } },
    { "role": "Window", "name": "...", "bounds": { "...": "..." } }
  ],
  "text": "可选：该元素内 OCR 文本",
  "crop": { "imageRef": "cache://selection/1187/crop.png" },
  "context": { "imageRef": "cache://selection/1187/context.png" }
}
```

设计要点：

- **多重定位键**：`hwnd + role + name + automationId + bounds + path`，不依赖单一 UIA 路径；
- **坐标系显式**：UIA 返回物理像素，必须声明虚拟桌面原点和 DPI 感知模式，否则 agent 会和截图错位；
- **新鲜度显式**：`snapshotId / capturedAt`，agent 自己判断是否过期；
- **大对象不内联**：图片优先给缓存引用，文本超阈值截断并标 `truncated`。

### 6.2 `resolve(elementKey)`

**输入**：`inspect` 返回的 `target + element + path` 子集。

**输出**：

```json
{ "found": true, "element": { "...": "..." }, "snapshotId": "...", "capturedAt": "..." }
```

用途：agent 拿到旧 key 后重新定位；`found=false` 表示元素已失效。
`resolve` 仍然是**只读**，不做任何动作。

### 6.3 与截图的关系

- `inspect` 的裁剪图和上下文图来自当前帧；
- 如果 agent 同时需要整屏，用单独的 `capture.screen` / `capture.window`（S3）；
- 不做「选区 UI 的远程驱动」：选区是人的交互，agent 传坐标即可。

---

## 七、传输与接入

### 7.1 绕不开的约束

Sparrow 是**单实例 + 托盘常驻**（`Local\Sparrow.SingleInstance`，`ShutdownMode.OnExplicitShutdown`）：

- 不能把 `Sparrow.App.exe` 直接配成 stdio MCP server——第二个实例会撞互斥锁直接退出；
- stdio MCP 需要**独立桥接进程**（不拿锁），通过命名管道连到正在运行的托盘实例；
- 或者直接由托盘实例提供**本地 HTTP / 命名管道**服务。

### 7.2 选项对比

| 方式 | 适合谁 | 优点 | 缺点 |
|---|---|---|---|
| **CLI** | 任何能执行命令的 agent | 最简单、零协议依赖 | 参数拼接；图片/进度/取消麻烦 |
| **MCP（stdio）** | 已支持 MCP 的 agent | 工具发现 + schema + 结构化结果 | 需要桥接进程 |
| **MCP / HTTP** | 远程或多客户端 | 不绑定进程 | 协议实现更重 |
| **命名管道** | 自研 agent | 轻、快、无端口 | 非标准 |
| **本地 HTTP** | 通用 | 易调试、可流式 | 端口/鉴权/防火墙 |

### 7.3 建议顺序

1. 定义 JSON 契约（与传输无关）；
2. **CLI 做基线**：`sparrow run <tool> --json`；
3. **MCP 适配器**：给 OpenCode / Claude / DeepSeek Harness 这类客户端；
4. HTTP 按需，不提前做。

---

## 八、授权与安全

| 项 | 要求 |
|---|---|
| 按工具授权 | S1 默认可用；S2/S3 首次调用需确认 |
| 逐次确认 | 屏幕 / 剪贴板读取每次或每会话确认 |
| 审计 | 记录工具、参数摘要、目标、结果、耗时 |
| 本地绑定 | `127.0.0.1` / 命名管道，不监听公网 |
| 密钥隔离 | agent 永远拿不到 AI Key |
| 结果不可信 | 屏幕内容可能含 prompt injection；Sparrow 只提供数据，不执行指令 |

**信息本身就是权限。** 能读屏幕、元素、剪贴板，就能被滥用，所以端点必须默认最小权限。

---

## 九、分阶段落地

| 阶段 | 交付 | 出口条件 |
|---|---|---|
| **P0** | 契约 + CLI + 3 个纯函数工具 | 任一 agent 能调通并拿到 JSON |
| **P1** | MCP 适配器 | OpenCode / Claude 能发现并调用 |
| **P2** | `inspect(point)` | agent 能定位元素并复述其类型/名称/位置 |
| **P3** | `resolve` + 截图/OCR | 元素失效后可重新定位 |
| **P4** | 动作（另议） | 单独设计、单独评审，默认不做 |

每一阶段都必须先满足出口条件，再进入下一阶段。

---

## 十、验收标准

用 `scope.md` 的三问过滤器（换成 agent 语境）：

1. 有**真实的外部 agent** 每周调用吗？
2. 调用后用户**少做了一步**吗？（比如不用再手动复述「我指的是哪个按钮」）
3. 它让 Sparrow **更锋利**，还是只是**更大**？

达不到就停在 P0，**不动主线**。主线仍是 `roadmap.md` 里的 M2（图片 → SVG 描摹）。

---

## 十一、开放问题

- 第一个接入的 agent 是哪个（OpenCode / Claude / DeepSeek Harness / 自研）？
- S1 的「纯函数工具」清单要包含哪些（是否需要 `Requires` 过滤）？
- 图片走 base64 还是本地缓存路径？
- 是否需要跨平台（macOS / Linux），还是只做 Windows？
- 授权模型：按工具、按来源、按会话，还是组合？
- `resolve` 是独立工具，还是 `inspect` 的一个参数？

---

## 十二、参考

- `docs/context-model.md`：六种入口 → 同一个 `ToolContext`
- `docs/scope.md`：三问过滤器、永久不做的清单
- `docs/roadmap.md`：M2 是当前主线
- `src/Sparrow.Plugin.Sdk/ToolDescriptor.cs`：`Accepts / Produces / Requires / Parameters`
- `src/Sparrow.Plugin.Sdk/ToolContext.cs`：输入载体与参数
- `src/Sparrow.Plugin.Sdk/IToolHost.cs`：当前宿主能力边界
- `docs/plans/2026-11-23-capture-element-selection-alignment-design.md`：元素级选区与 UIA 路径的来源
