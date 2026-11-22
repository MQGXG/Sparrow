# 剩余标注工具与工具条自定义：实现方案

覆盖 `docs/reference-parity.md` 批次 5（剩余 5 个工具）和批次 6（自定义布局）。

现状：批次 5 已完成 7/9（直线、橡皮擦、聚光灯、放大镜、水印、测距、括号），批次 6 未开始。
本文只写**怎么做**，不重复参考实现的调研。

---

## 一、共同前提

后面 4 个工具都要画文字，先把两件事定下来，避免每个工具各写一套。

### 1.1 文字标签（距离 / 角度 / 气泡 / 水印共用）

预览和导出必须用同一套度量，否则标签会错位。

| 场景 | 预览（Avalonia） | 导出（Skia） |
|---|---|---|
| 纯文字 | `TextBlock` | `canvas.DrawText` + `SKFont` |
| 带底标签 | `Border` + `TextBlock` | 先 `DrawRoundRect` 再 `DrawText` |
| 字号 | DIP | 同 DIP（画布已按 scale 缩放） |
| 内边距 | `Padding = 4,2` | `TextSize + 4/2` 的手算矩形 |
| 字宽估算 | 用 `EstimateTextWidth`（已有） | `font.MeasureText` |

约束：**同一个 `FontSize`，预览用 `TextBlock.FontSize`，导出用 `SKFont(size)`**。
雅黑在两边的度量差 1–2px，所以标签底必须留 4px 内边距，不能贴边。

### 1.2 多步输入（只有「角度」需要）

现有流程是「按下 → 拖动 → 松开 = 提交」。角度需要两次拖拽，所以引入一个
**草稿状态**，不改现有 `_pendingAnnotation` 语义：

```csharp
private AngleDraft? _angleDraft;   // 顶点 + 第一条边，等第二条边

private sealed record AngleDraft(Point Vertex, Point First);
```

- 第一次拖：写 `_angleDraft`，不产生标注，提示条显示「再拖一条边定角度」。
- 第二次拖：松手时用 `_angleDraft` + 当前点构造 `AngleAnnotation`，清草稿。
- `Esc` / 右键 / 切工具：清草稿。

---

## 二、逐个工具

每个工具都按「模型 → 交互 → 预览 → 导出 → 属性 → 键 → 测试」写。

### 2.1 水印（Watermark）

| 项 | 设计 |
|---|---|
| 模型 | `WatermarkAnnotation(Rect Area, string Text, uint Color, double FontSize, double Opacity, double Angle, double Spacing)` |
| 交互 | 拖出一个区域当平铺范围；单击（<8 DIP）时取整个选区 |
| 预览 | 区域内按 `Spacing` 平铺 `TextBlock`（限 200 个，超出只画前 200） |
| 导出 | `canvas.Save() → clipRect(area) → rotate(angle, area.Center) → 双重循环 DrawText → Restore` |
| 文字 | 默认 `"Sparrow"`；双击水印 → 弹输入框（复用 `ShowTextEditor` 的定位逻辑） |
| 属性 | 字号（S/M/L/XL 复用现有分段）、透明度、颜色 |
| 键 | `W` |
| 测试 | 导出后区域内出现非白像素、区域外保持白；角度 0 与 45 各一条 |

风险：平铺数量按 `面积 / Spacing²` 估，超大区域要限流（>5000 条直接跳过），否则导出卡死。

### 2.2 测距（Distance）

| 项 | 设计 |
|---|---|
| 模型 | `DistanceAnnotation(Point From, Point To, uint Color, double Width, string Label)` |
| 交互 | 拖一条线；按住 `Shift` 约束水平/垂直/45°（复用拖拽吸附的现有工具函数） |
| 长度 | `Length = 距离(DIP) × _scaling`，单位跟随选区工具条的 px/DIP 切换，**在创建时算好 Label** |
| 预览 | 线 + 两端 8px 垂直刻度 + 中点标签（黑底白字，偏移 12） |
| 导出 | 线 + 刻度 + 标签 pill（`#B3000000` 底 + 白字） |
| 属性 | 颜色、线宽、线型 |
| 键 | `D` |
| 测试 | 拖 100 DIP、缩放 1 时 Label == "100 px"；导出中点附近有深色底 |

已知限制：创建后切单位，Label 不会跟着变（写进文档）。

### 2.3 括号（Bracket）

| 项 | 设计 |
|---|---|
| 模型 | `BracketAnnotation(Rect Rect, bool OpenRight, uint Color, double Width, double Depth)` |
| 交互 | 拖矩形；`OpenRight = 终点在起点右侧`，即 `[` 或 `]` |
| 深度 | `Depth = min(24, Rect.Height / 3)`，不暴露到 UI |
| 预览 | `Polyline` 三点（上横 → 竖 → 下横） |
| 导出 | `SKPath` 同形状，`StrokeCap = Butt`（圆头会显得软） |
| 属性 | 颜色、线宽、线型 |
| 键 | `B` 已被「识别框模式」占用 → 用 `Shift+B` 或 `[`；**方案：`[`** |
| 测试 | 左侧竖线像素命中、右侧区域无像素 |

### 2.4 对话气泡（SpeechBubble）

| 项 | 设计 |
|---|---|
| 模型 | `SpeechBubbleAnnotation(Rect Rect, Point Tail, string Text, uint Color, double Width, double FontSize, bool Fill)` |
| 交互 | 拖出气泡框；`Tail` = 按下点（尾尖指向起点） |
| 文字 | 双击气泡 → 复用 `ShowTextEditor`，但落点是气泡内边距处；空文本也允许提交 |
| 预览 | `Border`（圆角 10）+ 三角形尾 + 居中 `TextBlock` |
| 导出 | 圆角矩形 + 三角尾（同一个 path，避免接缝）+ 文本居中 |
| 属性 | 颜色、线宽、填充（无/淡/实）、字号 |
| 键 | `T` 已被文字占用 → 用 `Shift+T` 或 `Q`；**方案：`Q`** |
| 测试 | 边框像素 + 文本区域有非白像素；尾尖方向正确 |

### 2.5 角度（Angle）

| 项 | 设计 |
|---|---|
| 模型 | `AngleAnnotation(Point Vertex, Point A, Point B, uint Color, double Width, string Label)` |
| 交互 | 两次拖拽（见 1.2），第二次松手提交 |
| 角度 | `acos(单位向量点积)`，取 0–180，Label = `"{deg:0}°"` |
| 预览 | 两条射线 + 圆弧（半径 36）+ 弧外侧标签 |
| 导出 | `DrawLine` ×2 + `DrawArc`（`SKPath.AddArc`）+ 标签 |
| 属性 | 颜色、线宽 |
| 键 | `A` 已被箭头占用 → `Shift+A`；**方案：`G` 已被滚动截图占用 → 用 `Shift+G`** |
| 测试 | 两条边垂直时 Label == "90°"；`> 179°` 不出现 NaN |

风险最高：多了草稿状态，容易和「边框优先」「右键回退」互相干扰。
`Esc` 的优先级要排在最前：先清草稿，再退工具。

---

## 三、批次 6：自定义布局

### 3.1 数据模型

```csharp
// AppSettings.cs
/// <summary>工具条布局：每个 position 用 "|" 连接工具 id，例如 ["ellipse|rectangle"]。</summary>
public List<string> CaptureToolPositions { get; init; } = [];
```

- 空列表 = 用默认布局（当前三组 + 独立工具），保证老配置能升级。
- 工具 id 用枚举名（`Rectangle`、`Ellipse`…），大小写不敏感。

### 3.2 渲染

把 `BuildToolGroups` 从硬编码改成读布局：

```
默认：["Ellipse|Rectangle", "Arrow", "Highlighter|Pen", "Mosaic", "Eraser", "Spotlight", "Magnifier", "Number|Text"]
```

- 长度 1 的 position = 普通按钮（无三角、无弹出）。
- 长度 ≥ 2 = 折叠组，入口 = 最后一项。
- 未知 id / 空项跳过；全部无效则回落默认。

### 3.3 编辑器（MVP）

入口：**右键拖拽把手** → 「工具布局」子菜单。

```
☑ 矩形        ↑ 上移   ↓ 下移
☑ 椭圆
☑ 箭头
...
[恢复默认]
```

- 勾选 = 显示/隐藏（隐藏的写进 `More` 列表，落到「更多」菜单里）。
- 上下移 = 在 position 数组里交换。
- 不做拖拽排序（v1），菜单已经能表达顺序。
- 关闭截图窗口时写回 `AppSettings`，和现有的标注样式持久化走同一条路。

### 3.4 验收

- 隐藏一个工具 → 关闭再开 → 仍然隐藏。
- 把「椭圆」移到「矩形」前面 → 折叠组入口变成椭圆。
- 全部隐藏 → 工具条仍保留指针 / 撤销 / 完成。
- 布局数据损坏（乱码 id）→ 回落默认，不崩。

---

## 四、顺序与风险

| 顺序 | 工具 | 依赖 | 风险 |
|---|---|---|---|
| 1 | 水印 | 文字标签 | 低（平铺限流要写） |
| 2 | 测距 | 文字标签 | 低 |
| 3 | 括号 | 无 | 低 |
| 4 | 气泡 | 文字标签 + 文本编辑 | 中（尾形与圆角接缝） |
| 5 | 角度 | 多步输入 | **高**（状态机） |
| 6 | 自定义布局 | 折叠组 | 中（持久化兼容） |

每个工具都要有：导出像素断言、命中/拖动/包围盒、撤销重做覆盖、文档一行说明。

---

## 五、明确不做

- 水印的模板/批量（参考有，本轮不做）。
- 角度的「360° 类型」和弧度单位（只做 0–180 度）。
- 气泡的「思考气泡 / 文本块」变体（只做矩形气泡 + 三角尾）。
- 布局的拖拽排序和「图钉」交互（只做菜单）。
- 任何工具做成插件：这些都是截图会话内的绘图能力，留在宿主（理由见 `reference-parity.md` §3.9）。

---

## 六、验收总量

- 批次 5 结束时：9 个新工具 + 原有 8 个 = 17 个工具，测试从 611 涨到约 630。
- 批次 6 结束时：布局可持久化，默认布局与现在一致，`606+` 条测试不回归。
