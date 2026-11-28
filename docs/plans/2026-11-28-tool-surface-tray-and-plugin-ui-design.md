# 工具形态、托盘入口与插件界面 · 设计

日期：2026-11-28
状态：已确认（2026-11-28）

## 背景与问题

这一轮讨论从三个具体疑问出发：

1. **窗口置顶为什么在工具墙？** 它没有输入、没有参数、没有可继续传递的结果，
   却和 JSON 格式化、视频裁剪一样占一张 236×148 的卡片。为什么不做成托盘项？
2. **工具和快捷键能力能不能让用户自己加进托盘？** 有的工具适合常驻，有的只适合搜。
3. **为什么点进每个工具界面都差不多？** 这些插件能不能像 Word 插件那样有自己的界面？

这三个问题表面无关，实际指向同一个缺口：**Sparrow 只有一种工具形态，也只有一种工具表面。**

## 决策记录（2026-11-28）

| 决策点 | 结论 |
|---|---|
| 没有页面的工具（如窗口置顶） | **不进工具墙**；只出现在托盘与命令面板 |
| 工具墙定位 | 只放「有输入 / 有输出」的页面型工具 |
| 托盘可固定内容 | 工具 + 宿主动作（工作区 / 截图 / 录屏 / 局域网 / 剪贴板面板）都允许 |
| 托盘默认项 | 现有 5 条宿主动作 + **窗口置顶** + **剪贴板面板** |
| 「配了热键自动进托盘」 | 删除这条隐藏规则 |
| 工具界面归属 | 由插件决定；但通过宿主提供的 UI 契约组合，不直接引用 Avalonia |
| 转换型工具 | 继续用宿主统一表面，不追求「每个都不一样」 |
| 插件界面优先级 | 只给「统一表面明显不合适」的工具做（时间轴 / 拾取 / 会话） |
| UI SDK 开放范围 | 第一期只给内置插件；第 3 档「任意 Control」永久只给受信任插件 |
| 第一个 UI SDK 试点 | **视频裁剪**（时间轴） |

关于「每个工具长得不一样」：它不是目标，而是结果。判断标准不是「界面是否不同」，
而是「这个工具的交互是否被统一表面扭曲了」。JSON 格式化用同一套输入框+结果区就是最优解；
视频裁剪需要时间轴，统一表面才显得别扭。所以先决定**哪些工具需要离开统一表面**，
而不是让 13 个工具各自长一副样子。

## 现状事实（来自代码）

### 1. 13 个工具里只有 1 个「异类」

| 工具 ID | Accepts | Produces |
|---|---|---|
| `sparrow.window.pin` | `None` | `Text` |
| `sparrow.dev.base64` | `Text` | `Text` |
| `sparrow.json.format` | `Text` | `Text` |
| `sparrow.text.translate` | `Text` | `Text` |
| `sparrow.svg.prompt` | `Text` | `Svg` |
| `sparrow.ocr.image` | `Images` | `Text` |
| `sparrow.images.convert` | `Image` | `Image` |
| `sparrow.images.resize` | `Image` | `Image` |
| `sparrow.files.hash` | `Files` | `Table` |
| `sparrow.video.trim` | `Files` | `FileSet` |
| `sparrow.video.compress` | `Files` | `FileSet` |
| `sparrow.video.toGif` | `Files` | `FileSet` |
| `sparrow.video.frames` | `Files` | `FileSet` |

结论：**现在不需要为「动作型工具」造一套系统**。12/13 都是输入→输出的转换型，
窗口置顶是唯一特例。先做最小分流，等第二个特例出现再抽象。

### 2. 托盘已经能放工具，但藏得很深

- 配置层：`AppSettings.TrayMenu` + `TrayMenuItemConfig { Tool = "sparrow.window.pin" }`
- 渲染层：`TrayMenuPlanner` 把 `tool` 项变成菜单项
- 执行层：`TrayService` → `ExecuteToolHotkeyAsync`（`App.Pipelines.cs:302`），
  **不打开工作区**，短结果用 toast，长结果进命令面板
- 输入来源：`PaletteViewModel.BuildContext` 从剪贴板构造；`Files` 类直接返回
  「文件类工具请在工作区里使用」

缺的三样：

| 缺失 | 现状 |
|---|---|
| 编辑 UI | 只能手改 `settings.json` |
| 卡片入口 | 不能从工具卡片「固定到托盘」 |
| 排序/分隔线 | 配置支持，界面没有 |

还有一个**隐藏规则**：空配置时，默认菜单只自动加入「配了热键」的工具
（`TrayMenuPlanner.BuildToolEntries` 遍历 `toolHotkeys`）。用户不配热键就永远看不到工具项，
也猜不到原因。

### 3. 工具台只有一个外壳

- `ITool` 只有 `Descriptor` + `ExecuteAsync` 两个成员
- `ToolParameter` 注释：「宿主据此自动生成控件，插件不提供任何 UI」
- `docs/plugin-model.md`：「任何 Avalonia 类型 → 插件不拥有 UI」
- `PaletteViewModel.FormatPayload` 把 `TablePayload` / `FileSetPayload` 全拍平成字符串

所以工具台必然是：

```text
参数（最多 5 个自动控件） + 输入 + 执行 + 结果文本
```

两个 `Accepts`/`Produces` 相同的工具，界面除了文案完全一样。这不是 bug，是缺少「表面」。

### 4. 插件加载的现有边界

`PluginLoadContext.Load`（`src/Sparrow.App/Plugins/PluginLoadContext.cs:23`）只有两条规则：

1. `Sparrow.Plugin.Sdk*` → 交给默认 ALC（否则 `ITool` 类型不一致，插件静默不注册）
2. 其余 → 先查插件目录；查不到返回 null → 落回默认 ALC

因此**只要插件不自带 Avalonia 副本**，它引用的 `Control` 与宿主的 `Control` 是同一个类型，
宿主可以 `ContentControl.Content = pluginControl` 直接渲染。当前插件输出目录里确实只有
插件自己的 DLL，这个前提目前成立，但没有构建期保护。

`ToolRunner` 用 `Task.Run` 调 `ExecuteAsync`（`ToolRunner.cs:40`），
所以**插件不能在 `ExecuteAsync` 里创建控件**。

## 设计目标

1. 让「纯动作」工具有正确的入口：不进工具墙，只出现在托盘与命令面板
2. 让用户自己决定托盘里放什么，宿主动作和工具都能固定，并给一组默认项
3. 让转换型工具按输入/输出形态呈现合理表面，不改插件 SDK
4. 为「插件自带界面」定一条可行、可卸载、可版本化的边界（先 UI 契约，后任意控件）

## 非目标

- 本期不向第三方插件开放「任意 Avalonia Control」
- 不要求每个工具都有独立界面；转换型工具继续共用统一表面
- 不改 `ITool` 执行协议（插件无 UI 也能跑）
- 不做插件市场、签名、权限弹窗

## 概念模型

| 形态 | 定义 | 适合入口 | 例子 |
|---|---|---|---|
| **Transform** | 输入 → 输出，可重复、可串联 | 工具墙 + 工具台 + 命令面板 | JSON、Base64、OCR、视频 |
| **Action** | 无输入，一次系统副作用 | 托盘 + 命令面板 + 热键 | 窗口置顶 |
| **Session** | 有持续状态，需要连续操作界面 | 独立窗口 / 浮窗 / 托盘常驻 | 录屏、时间轴裁剪 |

推导规则（优先用现有字段，不急着加 SDK 字段）：

```text
Accepts == None                          → Action
Accepts in {Image, Images, Text, Files}  → Transform
需要持续状态 / 分步交互                  → Session（目前没有，先不实现）
```

## 方案 A：工具形态分流

### A1. 本期做法

- `Accepts == None` 视为 Action
- **Action 工具不进工具墙**：没有页面，就不该占页面
- 它们出现在三处：托盘（默认固定）、命令面板（可搜索）、可选全局热键
- 「继续上次」chips 也过滤掉 Action，否则点开没有页面可回
- 命令面板、热键、搜索行为不变

发现性由托盘默认项兜底：新用户第一次打开托盘就能看到「窗口置顶」，
而不是在墙上找一张没有输入框的卡片。

实现落点：

| 位置 | 改动 |
|---|---|
| `WorkspaceViewModel.RefilterTools` | 过滤掉 `Accepts == None` 的条目 |
| `WorkspaceViewModel.RebuildRecentTools` | 同样跳过 Action，避免 chips 里出现无页面工具 |
| `WorkspaceViewModel.ToolCount` | 只统计墙上可见工具 |
| 命令面板 `ToolRegistry.Search` | **不过滤**，保证可搜、可执行 |

### A2. 何时引入显式字段

出现以下任一情况时，再给 `ToolDescriptor` 加 `Presentation`：

- 出现第二个 Action 工具，且无法用 `Accepts == None` 判断
- 出现第一个 Session 工具（录屏或时间轴裁剪）
- 需要区分「Action 是否可重复执行」「Session 是否单例」

字段草案：

```csharp
public enum ToolPresentation { Transform, Action, Session }

// ToolDescriptor
public ToolPresentation Presentation { get; init; } = ToolPresentation.Transform;
```

必须是**带默认值的增量字段**，避免现有插件全部失效。

### A3. 窗口置顶的落位（已决定）

| 候选 | 优点 | 缺点 |
|---|---|---|
| 保留卡片，加「动作」标签 | 改动最小 | 仍然是 236×148，没解决形态错位 |
| 降级成「快速动作」chip | 保留发现性 | 墙上仍然留了一个非工具区块 |
| **完全移出墙，只进托盘 + 面板** | 形态正确、墙更纯粹 | 新用户不主动开托盘就不知道 |

**决定：完全移出。** 通过「托盘默认固定 + 命令面板可搜」补发现性。

## 方案 B：托盘成为用户可编辑入口

### B1. 交互

- 工具卡片右键 → 「固定到托盘 / 从托盘移除」（Transform 工具也可以固定，比如 JSON 格式化）
- 命令面板里对工具的动作同样提供这一项
- 设置页也能添加**宿主动作**：打开工作区、截图识别、屏幕录制、局域网互传、剪贴板面板
- 固定后写入 `AppSettings.TrayMenu`，下一次托盘初始化生效（或热重载菜单）

### B2. 设置页

新增「托盘」段（和「快捷键」并列）：

- 列出当前菜单项，可拖动排序
- 可加分隔线、删除
- 可从两个来源添加：
  - 宿主动作（固定几项，见 B3）
  - 工具（只列出**可无头执行**的工具，见 B4）
- 明确提示：`配了热键 ≠ 自动进托盘`

### B3. 默认菜单

现在空配置会生成 5 条固定项 + 所有「配了热键」的工具。改为：

```text
搜索工具…            Ctrl+Alt+S
剪贴板面板…          Ctrl+Alt+B
截图识别…            Ctrl+Alt+A
屏幕录制…            Ctrl+Alt+Shift+R
局域网互传…
窗口置顶…            （Action 工具，默认固定）
工作区…
────────────
退出
```

- 宿主动作默认项保持不变，另加**窗口置顶**与**剪贴板面板**
- 剪贴板面板目前只有全局热键（`Ctrl+Alt+B`）入口，新用户不知道；
  托盘菜单是它最自然的常驻位置，成本只需新增一个 action kind
- 配了热键的工具**不再自动出现**（热键本身已经是入口，不必重复占菜单）

**迁移**：如果 `TrayMenu` 为空且 `ToolHotkeys` 非空，首次启动时把当前生效的菜单
反序列化写入 `TrayMenu`，之后不再依赖自动追加。这样老用户不会突然少一堆菜单项。

### B4. 哪些工具能进托盘

托盘执行是**无头 + 剪贴板输入**（`PaletteViewModel.BuildContext`）：

| Accepts | 托盘可用 | 说明 |
|---|---|---|
| `None` | ✅ | 窗口置顶 |
| `Text` | ✅ | 取剪贴板文本 |
| `Image` / `Images` | ✅ | 取剪贴板图片 |
| `Files` | ❌ | 没有文件来源，直接报错 |

所以托盘添加器应按 `Accepts` 过滤，避免用户固定了却永远执行失败。

### B5. 验收

- 右键卡片能固定/取消，重启后保持
- 托盘菜单顺序与设置页一致
- 从托盘执行窗口置顶：不打开工作区，toast 显示结果
- 未配热键的工具也能出现在托盘
- 老配置升级后菜单项不丢失

## 方案 C：转换型工具的表面（宿主侧，零 SDK 改动）

**适用范围**：只针对 Transform 工具。Action 不进墙；Session 走方案 D。
这一节不追求「每个工具都不一样」，只修正「统一表面明显扭曲交互」的地方。

### C1. 输入表面（按 `Accepts`）

| Accepts | 表面 |
|---|---|
| `None` | 不进工具台（Action 只走托盘 / 面板），此档不适用 |
| `Text` / `RichText` | 现在的编辑框 |
| `Files` | 整宽拖放区 + 文件清单 + 计数 |
| `Image` | 拖放区 + 当前图预览 |
| `Images` | 图库条 + 逐图结果分区（OCR 已经在用） |

### C2. 结果表面（按 payload）

现在全部走 `FormatPayload` 拍平。改为：

| Payload | 渲染 |
|---|---|
| `TextPayload` | 文本 |
| `SvgPayload` | 已有的预览/源码切换 |
| `ImagePayload` | 大图 + 保存 |
| `TablePayload` | 只读表格 + 复制为 TSV |
| `FileSetPayload` | 文件卡列表 + 打开位置 |

`ToolDescriptor.Produces` 已经能声明，宿主只需按 payload 类型选模板，插件不动。

### C3. 参数区

参数区保持统一，不按工具特判。

### C4. 验收

- 文件哈希显示表格而不是等宽文本
- 视频工具的 FileSet 显示文件卡
- 图片工具结果区是大图而不是「图片 12345 字节」
- 窗口置顶不出现在工具墙/工具台，只能从托盘或命令面板执行

## 方案 D：插件自带界面

### D1. 可行性结论

**可行，且不需要 IPC 或第二个进程。** 前提是堵住两个洞。

### D2. 两条硬约束

**（1）类型身份**

插件若把 `Avalonia*.dll` 拷进自己的输出目录，会加载出第二份 `Control` 类型，
宿主强转失败。必须：

- 插件 csproj：`<Private>false</Private>` + `<ExcludeAssets>runtime</ExcludeAssets>`
- `plugins/Directory.Build.targets`：新增 guard，发现 `$(TargetDir)Avalonia*.dll` 就 `<Error>`
- `PluginLoadContext.Load`：把 `Avalonia` 前缀也直接 `return null`，强制使用宿主副本
- `Sparrow.Plugin.Ui*` 与 `Sparrow.Plugin.Sdk*` 一样加入「默认 ALC」白名单，
  否则 UI 契约里的基类也会分裂成两个类型

**（2）`avares://` 资源解析**

编译后的 XAML 会引用 `avares://Sparrow.Plugin.X/...`。Avalonia 的 asset loader
按程序集名解析，插件程序集在独立 ALC 里，能否稳定解析**需要 spike 验证**。
若不行，退路是：

- 插件用 `AvaloniaRuntimeXamlLoader` 从 `Stream` 读内嵌资源
- 或宿主注册自定义 `IAssetLoader`，按 ALC 找程序集

### D3. 接口设计

不要把 `Control` 塞进 `ITool`——命令面板、托盘、热键都是无头路径。

```csharp
public interface IToolViewFactory
{
    // 只会在 UI 线程调用；返回 null 表示使用宿主默认工具台
    Control CreateView(IToolViewContext context);
}
```

宿主判断 `tool is IToolViewFactory` 才启用自定义视图。这样：

- 工具台头部（返回、名称、分类、复制/执行）仍由宿主统一绘制
- 插件只接管 `WorkspaceWindow.axaml:969` 那个 `Grid` 的第 4 行（输入 + 结果区）
- 无头执行完全不受影响
- 卸载策略可以按「有没有视图」分开

### D4. 五个坑

| 坑 | 原因 | 对策 |
|---|---|---|
| 线程 | `ToolRunner` 用 `Task.Run` 调 `ExecuteAsync` | 视图只能由宿主在 UI 线程调工厂创建，禁止在 `ExecuteAsync` 里 new 控件 |
| 主题 | 宿主用 `DynamicResource` + 深浅色 | 发布薄的 `Sparrow.Plugin.Ui` 契约包，提供色板 key；禁止硬编码颜色 |
| 卸载 | 视觉树持有插件类型强引用 | 面板关闭 → 断开引用 → Unload → GC 断言；UI 打开期间不热卸载 |
| 版本 | Avalonia 大版本升级 | 锁 12.x；`sdkVersion` 跟随 bump |
| 安全 | 拿到 `Control` 等于拿到 UI 线程、视觉树、剪贴板 | UI 插件 = 完全受信任，只给内置/签名插件 |

### D5. 界面归属：三档

“长得不一样”应该由插件决定，这一点成立。但「插件自己画」不等于「插件直接拿到
Avalonia 控件树」——中间还有一层宿主提供的 UI 契约。

**第 1 档：宿主统一表面（Transform 默认）**

输入框 + 参数 + 结果。JSON、Base64、哈希、图片转换都在这一档。一致性、无障碍、
主题、卸载全部免费，插件零 UI 代码。这是正确解，不是妥协。

**第 2 档：UI SDK 组合（推荐给 Session / 特殊表面）**

宿主发布 `Sparrow.Plugin.Ui` 契约包，提供一组**宿主自己的**控件：

```text
PluginPanel / PluginSection / PluginField
PluginButton / PluginTable / PluginImage
PluginFileDrop / PluginTimeline / PluginThumbnailStrip
```

插件用这些控件自由组合布局，宿主保留主题、DPI、键盘、无障碍和卸载权。
视频裁剪可以用 `PluginTimeline`，窗口置顶可以用 `PluginHint` + 一个按钮，
OCR 可以用 `PluginThumbnailStrip`。这是最接近「Word 任务窗格」的形态：
宿主给框和积木，插件决定怎么摆。

**第 3 档：任意 Avalonia Control（仅内置 / 受信任）**

兜底方案。需要 D2 的两个 guard、D3 的工厂接口、D4 的生命周期。
一旦插件能 new 任意控件，安全边界就等于「完全信任」，不再适合第三方分发。

**优先级**：先把需要离开统一表面的工具列出来，再决定它是第 2 档还是第 3 档。
目前只有视频裁剪明确需要第 2 档；窗口置顶按 A3 已经不需要页面。

### D6. Spike 验证清单（先做，不接正式 SDK）

用一个临时插件验证四件事：

1. 插件程序集里的 `UserControl` 能否被宿主 `ContentControl` 直接显示
2. `avares://` 资源能否解析；不能则验证 `Stream` 方案
3. 关闭视图后 `PluginLoader.Unload` + `GC.Collect` 能否让 ALC 的 `WeakReference` 失效
4. 深浅色切换时 `DynamicResource` 是否跟随

四项全过，第 3 档才立项；任何一项不过，退回第 2 档。

### D7. 验收（第 2 档 UI SDK）

**“试点”指第一个真实迁移到 UI SDK 的插件**：不是做一个演示 demo，而是把线上工具换掉，
用它验证契约是否够用、卸载是否干净、主题是否跟随。试点过了，才把 SDK 推广到其他工具。

- `Sparrow.Plugin.Ui` 契约包能被插件引用，且构建产物里不出现 Avalonia 副本
- 视频裁剪用 `PluginTimeline` 组合出区间选择，替换掉现有 `Files` 通用表面
- 无头执行路径不受影响（命令面板/热键仍可执行原工具）
- 深浅色、DPI 缩放正常
- 插件卸载后 ALC 回收（沿用 M1–M5 的断言方式）

## 风险与取舍

| 风险 | 影响 | 缓解 |
|---|---|---|
| 为 1 个特例造形态系统 | 过度设计 | 先用 `Accepts == None` 推导，显式字段延后 |
| 插件 UI 破坏 ALC 回收 | 内存泄漏回归 | 只允许受信任插件；spike 先验证回收 |
| Avalonia 版本耦合 | 插件升级即碎 | 锁版本 + `sdkVersion` |
| 托盘菜单变长 | 可用性下降 | 用户显式固定 + 设置页可排序 |
| 工具台表面分叉 | 维护成本上升 | 表面由 `Accepts`/payload 驱动，不按工具名特判 |

## 分阶段实施

| 阶段 | 内容 | 退出标准 | 粗估 |
|---|---|---|---|
| **P0** | 托盘：卡片右键固定、设置页托盘段、宿主动作可固定、去掉热键自动追加 + 迁移 | B5 全部通过 | 1 天 |
| **P1** | 无页面工具移出墙：`Accepts == None` 不进 `VisibleTools` / chips，默认固定托盘 | 窗口置顶不在墙上，仍可搜、可从托盘执行 | 0.5 天 |
| **P2** | 转换型工具表面：输入按 `Accepts`、结果按 payload | C4 全部通过 | 1–2 天 |
| **P3** | 插件界面 spike（D6） | 四项验证有明确结论 | 1 天 |
| **P4** | 第 2 档 UI SDK + 视频裁剪试点 | D7 | 2–3 天 |
| **P5** | 第 3 档任意 `Control`（若 P3 通过，仅内置） | guard + 契约包 + 回收断言 | 3–5 天 |

P0–P2 不依赖任何 SDK 变更，可以先做；P3 只产出结论，不产出功能。

## 实施状态

- **2026-11-28 · P1 完成**：`Accepts == None` 的工具不再进 `VisibleTools`、「继续上次」与分类计数；命令面板行为不变。
- **2026-11-28 · P0 核心完成**：新增 `TrayMenuActionKind.Clipboard` 与 `"action": "clipboard"`；默认菜单加入剪贴板面板与窗口置顶；新增清单字段 `trayDefault`，宿主不再硬编码工具 ID；删除「配了热键自动进托盘」，并用 `trayMenuCustomized` 做一次性迁移。
- **2026-11-28 · P0 卡片入口完成**：工具卡片右键可固定 / 取消固定到托盘，立即写入设置并重建菜单。
- **待做**：设置页托盘编辑（拖动排序 / 增删 / 分隔线）；P2 转换型工具表面；P3–P5。

## 决策（2026-11-28 定稿）

| 问题 | 决定 |
|---|---|
| **托盘默认项** | **加「剪贴板面板」**。它目前只有 `Ctrl+Alt+B` 热键入口，新用户不知道；实现只需新增 `TrayMenuActionKind.Clipboard` 并接上已有的 `ToggleClipboardPanel` |
| **UI SDK 开给谁** | **第一期只给内置插件**。第 2 档受控，契约稳定后可开放；第 3 档「任意 Control」**永久只给受信任插件** |
| **第一个试点** | **视频裁剪**。它最需要时间轴，最能暴露统一表面的问题；OCR 已有图库分区，验证力度不足 |

### 什么时候重新考虑「开给第三方」

同时满足这三条再开放，不提前：

1. 出现真实的第三方插件作者（不是假想需求）
2. `Sparrow.Plugin.Ui` 连续两个版本没有破坏性变更
3. 插件签名/信任模型已经落地

如果 P4 的视频裁剪试点因为 ffmpeg 管线耦合太深而卡住，可以先用 OCR 做一次轻量验证，
但**不把它算作正式试点**——它暴露不出时间轴这类复杂控件的问题。

## 附：相关文件

- `src/Sparrow.Plugin.Sdk/ToolDescriptor.cs`：`Accepts` / `Produces` / `Parameters`
- `src/Sparrow.Plugin.Sdk/ITool.cs`：保持无 UI 的执行协议
- `src/Sparrow.App/Plugins/PluginLoadContext.cs`：ALC 共享规则
- `src/Sparrow.App/Plugins/PluginLoader.cs`：加载与卸载
- `src/Sparrow.App/Shell/TrayMenuPlanner.cs`：托盘菜单生成
- `src/Sparrow.App/Shell/TrayService.cs`：托盘渲染与执行
- `src/Sparrow.App/Settings/TrayMenuItemConfig.cs`：托盘配置结构
- `src/Sparrow.App/App.Pipelines.cs`：`ExecuteToolHotkeyAsync` 无头执行
- `src/Sparrow.App/ViewModels/PaletteViewModel.cs`：`BuildContext` / `FormatPayload`
- `src/Sparrow.App/Views/WorkspaceWindow.axaml`：工具台布局（第 969 行起）
- `plugins/Directory.Build.targets`：插件构建约束
- `docs/plugin-model.md`：宿主能力边界
