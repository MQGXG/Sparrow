# 2026-11-18 截图悬停选区（窗口级 + 控件级）

> 状态：已实现
> 2026-11-23 扩展为「命中路径 + 层级切换」，见
> `2026-11-23-capture-element-selection-alignment-design.md`。

## 一、需求

参考截图工具的做法：鼠标移动到某个窗口上就能快速框住它，单击即可截图 / OCR；
再进一步，鼠标停在按钮、输入框、列表项上时要能直接框住这个控件。
同时自由拖选必须保留，不能被智能框选抢掉。

## 二、交互规则

| 操作 | 结果 |
|---|---|
| 打开遮罩 | 立刻按当前光标位置显示候选（含控件级） |
| 鼠标移动 | 光标下最上层窗口显示虚线候选 + 标题/尺寸 |
| 继续移动 | 候选细化到光标下的完整命中路径，徽标标出类型（`按钮 · 保存`） |
| 滚轮 | 在命中路径内逐级放大 / 缩小（子元素模式） |
| 单击 | 选中当前路径层（控件优先，否则整窗），弹出动作栏 |
| 按住拖动 | 自由选区优先；拖动超过最小尺寸后候选消失 |
| `Tab` | 整窗 / 子元素模式切换 |
| `Ctrl+Tab` | 在光标下重叠的窗口之间循环（强制窗口级） |
| `Esc` | 有 OCR 结果先清结果，否则取消截图 |

智能框选默认开启，不额外占用修饰键。

## 三、实现

### 窗口级

- `WindowCandidates`：`EnumWindows` 枚举可见顶层窗口，按 Z 序（最上层在前）
  - 只排除显式传入的 HWND（截图遮罩及子窗口），同进程的钉图窗口仍可被选中
  - 排除最小化窗口、DWM cloaked、点击穿透的 layered 窗口
  - 可见矩形按显示器裁剪；桌面宿主（Progman/WorkerW/Shell）按屏拆分，普通跨屏窗口取并集
  - 返回物理像素 `PixelRect` 与标题
  - `Pick` 返回包含点的最上层窗口；`Next` 用于 Ctrl+Tab 循环
- `CaptureWindow`
  - 打开时枚举一次（冻结帧前后窗口位置一致）；截图期间每 500ms 刷新
  - `OnPointerMoved` 更新候选虚线框与徽标
  - `OnPointerPressed` 记下当前路径层并开始自由选区；`OnPointerReleased` 时若几乎没有拖动
    且有候选，则把选区设为该层

### 控件级

- 新增依赖 `Interop.UIAutomationClient`（UI Automation COM 互操作，netstandard2.0）
- `UiElementProbe`：专用 MTA 线程 + 逐层懒缓存，接口 `ProbeAsync(hwnd, point)`
  - 缓存请求：`TreeScope = Element | Children`、`ControlViewCondition`、
    `AutomationElementMode_Full`，一次带回 `BoundingRectangle / IsOffscreen / ControlType`
  - 根节点 `ElementFromHandleBuildCache`，子节点 `BuildUpdatedCache` + `GetCachedChildren`：
    **每层只付一次跨进程调用**，命中在内存里查表
  - 子节点按兄弟顺序从后往前命中（后面的更靠前）；等大 `Pane/Group` 结构节点回溯，
    解决 Chromium 冗余容器盖住内容分支的问题（Snow Shot 同款做法）
  - 每个子节点裁剪到窗口矩形，路径按「深 → 浅」保留，末项固定为窗口
  - 子节点多于 16 个建 64px 网格索引；大列表命中不逐个比较
  - 跳过窗口本身、无名布局容器；结构容器也保留在路径里，用户可逐层选择
  - 顶层探不到时枚举包含光标的子窗口（面积小的优先），解决 UWP/WinUI：
    顶层是 `ApplicationFrameWindow`，真正内容在 `Windows.UI.Core.CoreWindow`
  - 控件类型用 `CurrentControlType` 映射成中文（`按钮`/`输入框`/`列表项`/`文档`…），
    因为部分应用的 `LocalizedControlType` 是空串
  - 结果带 `UiElementStopReason`（预算/超时/失败/解码中/遍历上限）
- `MsaaElementProbe`：UIA 只给到窗口时兜底；从 `accHitTest` / `accChild` 下钻收集
  同形状的路径，超时窗口加入黑名单
- `ElementSelectionModel`：路径状态、整窗/子元素模式、显式选层、细化尾部校验
- 探测调度：**不做时间防抖**，每次移动立刻投递到 UIA 线程
  - 打开遮罩时先按当前光标位置出一次候选，不用先晃鼠标
  - 每次移动把 generation 往前推；worker 在遍历前读一次 generation，过时的任务直接丢掉
  - 遍历过程中每步再检查一次，鼠标扫过时不会白跑完整棵树
  - 光标还停在当前层里时直接返回，不投递任何探测
  - 结果回来后再次校验 generation / 窗口句柄 / 光标位置
- 单击：`_uiCandidateAtPress` 只有包含按下点时才生效，避免用过时的控件框

### 为什么不用 `AutomationElement.FromPoint`

遮罩窗口在最上层，`FromPoint` 永远返回遮罩自己（实测 `Name=SparrowCapture, Class=CaptureWindow`），
即使加 `WS_EX_TRANSPARENT` 也仍会命中。按 HWND 进目标窗口的树、自己用矩形命中，
既不受遮罩影响，也不需要临时隐藏遮罩造成闪烁。

## 四、验证

- 同一时刻对真实窗口取样（临时测试，验证后删除）：
  - VS Code：`选项卡 58×33`、`工具栏 404×33`、`图片 1907×631`
  - Edge：`文档 1112×583`
  - Snow Shot：`按钮 698×33`、`列表项 658×23`
  - 计算器（UWP）：`按钮 / 标准模式 233×47`、`文本 / 显示为 0 320×72`
- 耗时（同一窗口 30 次探测）：
  - 冷启动（每窗口第一次）：**23–96ms**
  - 热命中：**p50 0.0ms，p95 ≤ 0.2ms**
  - 端到端（鼠标停下 → 候选出现）：**1.4–1.8ms**（实测打点）
- 遮罩内实测日志：探测返回 `按钮 26×655` 并写入候选（`ui 按钮 26×655`）
- 构建 0 警告 0 错误；396 个测试通过

## 五、稳定性

- 压力测试（临时测试，验证后删除）：
  - 300 次随机点探测：p50 **0.01ms**、p95 7.8ms、p99 53ms、max 115ms（冷分支）
  - 200 个「快速扫动」任务一次性投递：全部结束，只有最后一个真正执行（其余在 worker 里被代际丢弃）
  - 释放时有 50 个挂起请求：全部完成，释放后新请求立刻返回 `null`，无卡死
  - 反复开关窗口 10 次：托管堆只涨 3MB，探测线程/COM 句柄没有累积
- 偶发失败（超时、应用忙）在下一个节点展开时重试一次，连续两次失败才放弃该分支
- 连接超时 300ms / 事务超时 800ms；单次遍历最多 80 步、每节点最多 600 个子节点
- 冻结帧是整屏大对象（4K 约 33MB）。抓图改成**容差比较 + 双缓冲复用**：
  - 原来要求两帧逐字节相等，任务栏时钟/光标闪烁会让 8 轮全跑满（实测 1053ms、+286MB）
  - 现在允许千分之一像素不同、最多 2 轮，实测 **231–256ms、+95MB**
  - 关窗时 `_frozenPixels` / `_frozenBitmap` 释放，工作集超过 600MB 在**后台线程**
    压缩一次 LOH：4 次开关实测 441→481→612→520MB（未处理时 703→884→933→1034→1072MB）
- 注意：`GCCollectionMode.Aggressive` 必须配 `blocking: true`，否则抛 `ArgumentException`
  （第一版写成 `blocking: false`，关窗时崩溃，已修）

## 六、边界

- Electron / Chromium 是否暴露细粒度控件取决于其无障碍树；没开时只能到文档区或窗口级
- Canvas / 游戏 / 自绘界面没有控件树，自然回落到窗口级
- 多屏不同缩放仍沿用主屏缩放做 DIP 换算（M1 已知限制）；候选矩形已按各显示器裁剪
- 窗口在冻结后移动不会再更新候选（位置按冻结帧计算）
- 缓存按窗口句柄保存到本次截图结束；界面在冻结帧之后发生变化时仍按缓存结果
