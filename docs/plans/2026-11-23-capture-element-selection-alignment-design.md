# 2026-11-23 元素级智能选区：对齐 Snow Shot

> 状态：已实现（Windows 侧）
> macOS AX 不在本期（Sparrow 只发 Windows）。

## 一、目标

把「控件级悬停选区」从“单个最深控件 + 窗口兜底”升级为与 Snow Shot 一致的
「命中路径 + 层级切换」模型：

- 一次探测返回从**最深的元素**到**外层窗口**的完整矩形路径；
- 滚轮在路径内逐级放大 / 缩小；
- `Tab` 在「整窗 / 子元素」两种模式间切换；
- 细化结果必须是在旧路径上原地展开，不能跳到不相关的位置；
- 控件矩形裁剪到窗口，跨屏 / 多显示器命中当前屏；
- 只排除截图遮罩窗口，不再排除整个进程（钉住的图片窗口也能被截取）；
- 用 `StopReason` 替代 `Completed` 布尔和按中文控件名判断重试。

## 二、模型（对照 Snow Shot）

Snow Shot：`ScreenshotIntelligentSelectionModel`
- `m_hitRects`：深 → 浅，末项是窗口；
- `m_index`：当前层；
- `m_mode`：Window / WindowSubElement / Disabled；
- `m_explicitSelection`：用户显式选层后，细化时按矩形匹配保留该层。

Sparrow：新增 `Services/ElementSelectionModel.cs`（纯 C#，可单测）
- `ApplyPath(path, bounds, minSize)`：新路径 → 默认取最深（元素模式）或末项（窗口模式）；
- `ApplyRefinement(path, bounds, minSize)`：新路径必须是旧路径的**尾部扩展**，否则拒绝；
- `SelectIndex` / `Step`：显式选层；
- `ToggleTarget`：整窗 ↔ 子元素；
- 用户显式选层后，细化按 `Bounds` 找回同一层。

## 三、探测改造

`UiElementProbe`：
- `Pick()` → `BuildPath()`：从命中节点向上收集每一层可框节点；
  - 排除根节点、与根同尺寸、与更深项同矩形；
  - 每层保留 `Bounds + ControlType`，最终结果只为最深两层查 `Name`；
  - 末项固定为窗口矩形；
- 每个子节点 `intersect_rect(child, window)`（对齐 Snow Shot 的 `cache.rs`）；
- `UiElementProbeResult` 改为 `(Path, StopReason)`；
- 细化命中时校验旧路径是新路径的尾部。

`MsaaElementProbe`：
- 从 `accHitTest` + `accChild` 下钻收集路径，同样裁剪 / 去重 / 补窗口；
- 返回 `(Path, StopReason)`；超时、无响应保留现有黑名单策略。

## 四、窗口快照

新增 `Services/CaptureWindowSnapshot.cs`：
- `Refresh(excludedHwnds, displays)`：一次 `EnumWindows` + DWM 可见矩形；
- 只排除传入的 HWND（含子窗口），同进程其它窗口仍可选；
- `MonitorCache`：`EnumDisplayMonitors`；跨屏普通窗口取并集，桌面宿主（Progman/WorkerW/Shell）按显示器拆分；
- `Pick(point)` 按 Z 序返回最上层；提供 `DisplayIdAt(point)`。

`CaptureWindow` 保留 500ms 重枚举（对截图期间新弹出的菜单更友好），
但改用快照 + 显示器裁剪 + 精确排除列表。

## 五、交互（有意的行为变更）

| 操作 | 旧 | 新 |
|---|---|---|
| 悬停 | 窗口 → 控件自动细化 | 窗口 → 命中路径，默认最深元素 |
| 滚轮（无选区） | 无 | 在命中路径内逐级切换（元素模式） |
| `Tab` | 循环重叠窗口 | 整窗 / 子元素模式切换 |
| `Ctrl+Tab` | 无 | 循环重叠窗口 |
| 单击 | 控件优先，否则窗口 | 当前路径层；无路径则窗口 |

## 六、失败与诊断

`UiElementStopReason`：`Complete / BudgetExhausted / DecodingPending /
ProviderTimeout / ProviderFailure / Cancelled / TraversalLimit`。

- `BudgetExhausted / DecodingPending / ProviderTimeout` → 可细化或重试；
- `ProviderFailure / Cancelled` → 保持窗口级，不重试；
- `TraversalLimit` → 结束。

保留“粗结果”重试（Chromium 懒建树），判断依据改为路径只有窗口 /
最深是文档或窗格且接近整窗，不再依赖中文控件名的硬编码。

## 七、测试

- `ElementSelectionModelTests`：路径顺序、窗口模式、显式选层、细化尾部校验、裁剪与去重；
- `WindowCandidatesTests`：排除/显示器裁剪/桌面拆分（`SelectionRects`）、同句柄不同屏；
- 现有 `MsaaElementProbeTests` 改为新返回类型；
- `dotnet build` + `dotnet test`。

## 八、实施结果

已落地：

- `ElementSelectionModel`（新）：深 → 浅路径、整窗/子元素模式、滚轮选层、
  粗探/细化中的路径扩展保留显式选层；
- `UiElementProbe`：`Pick` → `BuildPath`，路径末项窗口、每层裁剪到窗口、
  `UiElementStopReason`、超时识别、细化兼容；
- 单层子节点上限从 600 提到 65536，节点上限从 12000 提到 30000：
  网页长列表 / 大表格不会因为兄弟节点被截断而命中不到；
- 前景单次跨进程调用 168ms、细化 500ms（与 Snow Shot 的 foreground / refinement 对齐）；
- 细化使用独立 MTA worker（`RefinementProbe`），前景 worker 不被 1.5s 细化排队；
  前景结果后 80ms 再调度细化，光标一动立即取消；
- 细化阶段 `refreshTree` 重建 UIA 树：Chromium 前景拿到的空树 / 部分树不会跨阶段复用；
- 细化 progress 走 `ApplyRefinementPath`，只接受旧路径的扩展；
- 路径兼容从“严格尾部”放宽为“旧路径按序出现、末项窗口相同”，
  允许 UIA 细化时在旧层之间插入新层；
- `IsStructural` 只认 Pane/Group（Custom 归为具体控件），避免把自绘控件当容器回溯；
- **UIA 顶层只到窗口/文档/窗格/分组时，继续尝试包含光标的子窗口**：
  Chrome/Edge 的网页 UIA 树挂在 `Chrome_RenderWidgetHostHWND` 上，只查顶层窗口永远只能拿到 Document；
  子窗口路径更深时采用，并补回顶层窗口层；
- **浏览器首次粗结果触发一次细化**：`Chrome_WidgetWin_1` / `MozillaWindowClass` /
  `ApplicationFrameWindow` 等窗口如果只给窗口/窗格，等 80ms 后用 1.5s 预算重建树；
  等价于 Snow Shot 的 `AccessibilityPending`，不再用 MSAA 客户端去覆盖；
- **MSAA 先取 `OBJID_WINDOW`**（snow-shot 同款），只拿到窗口时才回退 `OBJID_CLIENT`；
  下钻上限 64 层（原 16 层会在 Chrome 深树上提前停住）；
- **MSAA 路径与可见子窗口矩形合并**（`merge_hit_paths` 同款），
  MSAA 只到客户端时仍保留子窗口/文档层；
- **每次鼠标移动都重新命中**（和 Snow Shot 一致），不再因为“还在同一控件内”就跳过：
  同一行里从空白移到链接、从容器移到按钮，都能继续细化；
- **点击使用当前可见路径层**：不再因为按下点比候选框偏 1px 就退回整窗；
- **遮罩窗口兜底排除**：即使没拿到遮罩 HWND，也按进程 + 标题/类名
  （`SparrowCapture` / `CaptureWindow`）排除，避免整屏遮罩命中自己；
- 细化路径从顶层 Document 切到渲染子窗口时，旧层不会出现在新路径里，
  允许「同窗口、更深」时按替换处理，避免一直卡在文档层；
- **调试诊断**：调试器附加或 `SPARROW_SELECTOR_DIAGNOSTICS=1` 时，
  把 UIA/MSAA 的路径和 `StopReason` 输出到 VS 输出窗口；
- `MsaaElementProbe`：`accHitTest` / `accChild` 路径收集，MSAA 只到窗口时用
  可见子窗口矩形兜底（对齐 Snow Shot 的 `merge_hit_paths`）；
- `WindowCandidates`：显式排除 HWND（含子窗口）、显示器裁剪、桌面按屏拆分、
  同句柄不同屏按矩形区分；
- `CaptureWindow`：滚轮切层、`Tab` 整窗/子元素、`Ctrl+Tab` 循环窗口、
  单击应用当前层、窗口层继续尝试探测内部元素；
- 删掉前景 350ms × 3 的粗结果重试：细化已经重建树，不再用旧缓存反复等；
- 测试：新增 `ElementSelectionModelTests`，扩展 `WindowCandidatesTests`，
  更新 `MsaaElementProbeTests`；全量 794 个测试通过。

暂缓：

- 多屏不同缩放：候选已按显示器裁剪，但单 overlay 窗口仍用主屏 `_scaling` 换算 DIP，
  混合 DPI 下框位置会偏；彻底解决需要每屏一个 overlay 窗口；
- UIA RawView 回退：当前先走 ControlView + 子窗口 + MSAA；实测仍拿不到网页元素时再加；
- macOS AX：超出 Windows 交付范围；
- 显式 `refresh` epoch：保留 500ms 重枚举（对新弹出菜单更友好），
  窗口快照语义（排除列表 / 显示器裁剪）已对齐。

## 九、实测：Edge 打开 GitHub 仓库页

用 `tools/SelectorProbe` 对真实 Edge 窗口（`MQGXG/Sparrow` 页面）直接探测：

```text
UIA-170 reason=Complete count=8
  链接 | refactor(ocr): worker 移入插件目录，收紧引用边界 | 1214,811 311x19
  数据项 | ... | 1198,800 406x41
  数据项 | ... | 837,800 903x41
  表格 | ... | 836,597 905x488
  分组 | main branch ... | 632,403 1625x921
  分组 | ... | 632,387 1625x937
  窗口 | ... | 632,387 1640x937      # Chrome 渲染子窗口
  窗口 | ... | 621,307 1662x1028     # 顶层窗口
```

结论：

- 当前 UIA 前景 170ms 已经能拿到网页里的 `链接 / 数据项 / 表格 / 分组`；
- MSAA 在这个页面只到 `客户端 / 窗口`，所以浏览器主要依赖 UIA；
- 之前浏览器只能选文档，是顶层窗口路径没有继续进入渲染子窗口；
  现在 `Probe` 会在粗层继续查 `Chrome_RenderWidgetHostHWND` 并补回顶层窗口；
- 遮罩窗口的标题/类名兜底排除，避免整屏遮罩挡住所有候选。

复现命令：

```powershell
dotnet run --project tools/SelectorProbe -c Debug -- <Edge 进程 Id>
```

## 十、Chromium 遮挡会关闭网页无障碍树 → 预建 UIA 树

### 根因（实测确认，非猜测）

遮罩把 Edge/Chrome 窗口完全盖住后，**Chromium 会关闭网页无障碍树**，
此时 UIA 只剩 `窗口 / 文档 / 窗格`，无论换 ControlView、进渲染子窗口还是重试都拿不到行、链接、按钮。

对比实验（同一 Edge 页面、同一坐标）：

| 条件 | UIA 返回 |
|---|---|
| 遮罩关闭 | 7 项，含 `global.json (File)` 等行级元素 |
| 遮罩打开 | 3 项，只有窗口 / 文档 / 窗格 |
| 先在无遮挡时建树，再盖遮罩 | 仍返回 7 项，含行级元素（树不会被销毁） |

结论：**必须在显示遮罩前、目标窗口还未被遮挡时先把树建好**。

### 方案

截图入口 `App.CaptureAndRecognize`：

1. 收集预建目标：光标下**所有**浏览器类窗口（不只最上层），最多 2 个。
   只看最上层会漏掉浏览器——从 VS 按 F5 后再触发时，最上层往往是 VS/任务栏，
   而浏览器仍在光标下方可见；
2. 光标下最上层的那个浏览器 `SetForegroundWindow` 提到前台，`await Task.Delay(300)`；
   其余保持在原位置，避免抢焦点/闪烁；
3. 对每个目标用 `MsaaElementProbe` 跑一次 IAccessible 激活（1200ms 预算）——
   Chromium 收到 IAccessible 请求才真正构建网页无障碍树；
4. `await Task.Delay(250)`；
5. `UiElementProbe.PrewarmAsync(hwnd, 1500ms)` 建快照（`_flatTrees`）；
6. 任一个预建成功才把实例交给 `CaptureWindow.ElementProbe`，全部失败则丢弃并退回逐层实时探测。

`PrewarmAsync` 的两个约束：

- Descendants 批量请求使用 **2s 专用超时 + 重试一次**（调试器附加 / 页面首次建树较慢）；
- 没建出扁平树时返回 **false**，不能假报成功，否则宿主会设置 `UsePrewarmedTree=true`，
  连带跳过 IAccessible 激活、`refreshTree` 和细化兜底。

`_flatTrees` 存 `FlatTree(elements, bounds)`，窗口矩形和元素一起保存：
预建第二个浏览器时 `EvictOtherTrees` 会清掉第一个的 `_trees`，扁平树不能依赖它。

`CaptureWindow` 的兜底：没有预建树时，浏览器窗口**首次探测前**先做一次 IAccessible
激活并等待 120ms（`_accessibilityActivated` 去重），再用常规路径探测。

诊断开关：`SelectorTrace` 默认关闭，只在附加调试器或
`SPARROW_SELECTOR_DIAGNOSTICS=1` 时写 `selector.log`（每次截图归档上一份到
`selector.prev.log`）。每窗口的预建结果另写一行到持久运行日志，便于现场排查。

### 坐标一致性

UIA 返回的是**物理像素**。诊断工具与 Sparrow 都必须是 per-monitor v2
（`SetProcessDpiAwarenessContext(-4)`），否则系统会按 DPI 缩放虚拟化坐标，
出现「工具能选中、应用选不中」的假象。

### 验证（Edge + GitHub 文件列表）

```text
prewarm descendants=737
prewarm flat=312 hwnd=0x10802
prewarm result=True
open ... prewarmed=True
flat hit hwnd=0x10802 point=1541,1070 flat=312 -> 列表项 ... > 窗格 > 窗口
apply hit current=列表项 1282,1045,543x61 layers=6
```

遮罩上高亮为文件行 `.config`，提示 `数据项 .config, (目录) 543 x 61`——行级选中与 Snow Shot 一致。

> 注意：取点必须落在元素矩形内。实测 `(1600,903)` 落在行顶 `y=924` 之上 10px 外，
> 只会得到 `文档`；`(1600,950)` 落在行内即选中 `列表项`。这不是坐标换算错误。
