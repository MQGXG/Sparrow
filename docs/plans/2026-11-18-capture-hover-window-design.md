# 2026-11-18 截图悬停选区（窗口级 + 控件级）

> 状态：已实现

## 一、需求

参考截图工具的做法：鼠标移动到某个窗口上就能快速框住它，单击即可截图 / OCR；
再进一步，鼠标停在按钮、输入框、列表项上时要能直接框住这个控件。
同时自由拖选必须保留，不能被智能框选抢掉。

## 二、交互规则

| 操作 | 结果 |
|---|---|
| 鼠标移动（无选区） | 光标下最上层窗口显示虚线候选 + 标题/尺寸 |
| 鼠标停稳约 120ms | 候选细化到光标下的控件，徽标标出类型（`按钮 · 保存`） |
| 单击 | 选中当前候选（控件优先，否则整窗），弹出动作栏 |
| 按住拖动 | 自由选区优先；拖动超过最小尺寸后候选消失 |
| `Tab` | 在光标下重叠的窗口之间循环（强制窗口级）；到末尾隐藏候选 |
| `Esc` | 有 OCR 结果先清结果，否则取消截图 |

智能框选默认开启，不额外占用修饰键。

## 三、实现

### 窗口级

- `WindowCandidates`：`EnumWindows` 枚举可见顶层窗口，按 Z 序（最上层在前）
  - 排除本进程窗口、最小化窗口、DWM cloaked、工具窗口、桌面壳层（Progman/WorkerW/任务栏）
  - 返回物理像素 `PixelRect` 与标题
  - `Pick` 返回包含点的最上层窗口；`Next` 用于 Tab 循环
- `CaptureWindow`
  - 打开时枚举一次（冻结帧前后窗口位置一致）
  - `OnPointerMoved` 更新候选虚线框与徽标
  - `OnPointerPressed` 记下候选并开始自由选区；`OnPointerReleased` 时若几乎没有拖动
    且有候选，则把选区设为候选

### 控件级

- 新增依赖 `Interop.UIAutomationClient`（UI Automation COM 互操作，netstandard2.0）
- `UiElementProbe.Probe(hwnd, point)`
  - `CUIAutomation.ElementFromHandle(hwnd)` + `ControlViewWalker` 递归子树
  - 用 **矩形命中** 判断光标在哪个控件里，返回面积最小的那个
  - 预算：深度 14、节点 260；`CurrentIsOffscreen != 0` 直接跳过
  - 跳过窗口本身；无名字的 `Pane` / `Group` / `Custom` 视为布局容器，不当候选
  - 结果 ≥ 窗口面积 95% 时退回窗口级，避免重复框同一块
  - 控件类型用 `CurrentControlType` 映射成中文（`按钮`/`输入框`/`列表项`/`文档`…），
    因为部分应用的 `LocalizedControlType` 是空串
  - 任何 COM 异常都返回 `null`，自动回落到窗口级
- 拖动 120ms 防抖用 `Task.Delay` + generation：
  - 每次移动把 generation 往前推，旧的等待醒来后自行作废（不依赖 DispatcherTimer）
  - 探测在 `Task.Run` 后台线程执行；完成后再次校验 generation / 窗口句柄 / 光标位置
  - 一次探测超过 2s 视为卡住，允许新的探测，避免被无响应应用拖死
- 单击：`_uiCandidateAtPress` 只有包含按下点时才生效，避免用过时的控件框

### 为什么不用 `AutomationElement.FromPoint`

遮罩窗口在最上层，`FromPoint` 永远返回遮罩自己（实测 `Name=SparrowCapture, Class=CaptureWindow`），
即使加 `WS_EX_TRANSPARENT` 也仍会命中。按 HWND 进目标窗口的树、自己用矩形命中，
既不受遮罩影响，也不需要临时隐藏遮罩造成闪烁。

## 四、验证

- 同一时刻对真实窗口取样（临时测试，验证后删除）：
  - VS Code：`选项卡 58×33`、`工具栏 404×33`
  - Edge：`文档 1112×583`
  - Snow Shot：`按钮 698×33`、`列表项 658×23`
- 遮罩内实测日志：探测返回 `按钮 26×655` 并写入候选（`ui 按钮 26×655`）
- 构建 0 警告 0 错误；396 个测试通过

## 五、边界

- Electron / Chromium 是否暴露细粒度控件取决于其无障碍树；没开时只能到文档区或窗口级
- Canvas / 游戏 / 自绘界面没有控件树，自然回落到窗口级
- 多屏不同缩放仍沿用主屏缩放（M1 已知限制）
- 窗口在冻结后移动不会再更新候选（位置按冻结帧计算）
