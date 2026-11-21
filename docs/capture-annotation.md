# 截图标注（设计稿）

> 结合两个参考：
> · **snow-shot**（Qt/C++）：工具条分组、布局紧凑、锚定放置（已用于 `docs/capture.md`）
> · **LumaShot**（Win32/C++）：**按工具切换的属性行**（`tool_properties.h` + `ui/toolbar.cpp`）、马赛克/箭头/画笔的完整参数

目标：截图工具条从「只能识别/复制」升级为「能画、能遮、能标序号」，并保持深灰 + 琥珀的视觉。

## 一、工具

| 工具 | 行为 | 参考 |
|---|---|---|
| 选择/移动 | 默认态：拖选区、拖动标注、拉控制点 | LumaShot `Tool::Select` |
| 矩形 | 拖拽出框；可调圆角与填充透明度 | 两边都有 |
| 椭圆 | 拖拽出圆/椭圆 | LumaShot |
| 箭头 | 起点→终点；箭头头部大小/样式可调 | LumaShot `model/arrow.cpp` |
| 画笔 | 自由、直线、连续直线三种轨迹；连续线条做平滑 | LumaShot `Pen` |
| 文字 | 点一下出输入框，提交后成为图元；字号/背景可选 | snow-shot |
| 马赛克 | 像素/模糊两种模式；涂抹/框选两种方式；密度可调 | LumaShot `Mosaic` |
| 序号 | 点一下递增编号，圆形/方形，可改颜色 | LumaShot `Number` |
| 高亮 | 半透明粗笔画，用于划重点 | LumaShot `Highlighter` |

第一版做：矩形、椭圆、箭头、画笔、文字、马赛克（6 个），序号与高亮放第二版。

## 二、工具条布局（合并两边）

> 2026-11-21 更新：工具按钮改成 **32×32 图标**（snow-shot 的 `kButtonSize`），
> 属性行只在选中工具时出现（LumaShot 的「属性卡跟着工具走」），组间加 1×18 分隔线。

```
┌ 工具 ──────────────────────────── | 属性（仅选中工具时）──────────────┐
│ ▭ ◯ ↗ ✎ ▤ ▦ T ①   | 撤销        | 色 ●●●●●  粗细 2 4 8  马赛克·像素 │
└──────────────────────────────────┴──────────────────────────────────┘
┌ 动作 ────────────────────────────────────────────────────────────────┐
│ 识别文字  翻译▾  复制图片  保存  固定  滚动  复制全文   | 取消        │
└──────────────────────────────────────────────────────────────────────┘
```

未选工具时只有两行（工具 + 动作），高度比原来少一行；选中工具后属性行展开。

**工具条可拖动**：工具行最左侧是拖动把手（`SizeAll` 光标）。拖过之后工具条不再跟随选区，
位置钳制在窗口内；这是每个截图窗口独立的状态，下次呼出重新按选区定位。

**窄窗口折叠**：窗口宽度 < 900 DIP 时，`复制图片 / 保存 / 固定 / 滚动` 收进「更多 ▾」菜单，
只留识别、翻译和取消。阈值在 `CaptureWindow.ShouldCompact`，布局切换在 `SetToolbarCompaction`。

关键点：

1. **属性行按工具切换**（取自 LumaShot）：选中矩形才出现圆角/填充；选中马赛克才出现像素/模糊、涂抹/框选；不把十几种参数全铺出来。
2. **三组 + 分隔线**（取自 snow-shot）：工具 / 属性 / 动作。动作区永远是「识别、翻译、复制、保存…」，不会因为切工具而移动。
3. **宽度不够时折叠**：属性行先收进「属性」按钮，再不行把低频动作收进「更多 ▾」（snow-shot 的 layout compaction）。
4. 放置规则沿用 `CaptureWindow.ComputeToolbarPosition`（右下锚定、放不下翻上方、钳制）。

## 三、数据模型

```csharp
// 纯数据，便于撤销与重放；坐标是 DIP，导出时乘 _scaling
abstract record Annotation;
record RectNote(Rect Rect, uint Color, double Width, double CornerRadius, double FillOpacity) : Annotation;
record EllipseNote(Rect Rect, uint Color, double Width, double FillOpacity) : Annotation;
record ArrowNote(Point From, Point To, uint Color, double Width, double HeadSize) : Annotation;
record PenNote(IReadOnlyList<Point> Points, uint Color, double Width, double Opacity, PenMode Mode) : Annotation;
record TextNote(Point Origin, string Text, uint Color, double FontSize, TextBackground Background) : Annotation;
record MosaicNote(Rect Rect, MosaicMode Mode, MosaicMethod Method, double Cell, int Strength) : Annotation;
```

- 撤销 = 弹出最后一条并重画（不做命令模式，图元都是不可变记录，够用）。
- 重做保留一个 `Stack<Annotation>`，画新图元时清空。
- 所有图元存 `List<Annotation>`，`OnRender` 按顺序画。

## 四、导出合成

**不要**用 Avalonia 截图渲染（高 DPI 会发虚）。用 SkiaSharp 直接画到物理像素：

```
1. CaptureSelectionAsync 拿到选区 PNG（原尺寸）
2. 新建 SKCanvas(png)，按 _scaling 缩放坐标系
3. 依次画图元（马赛克先做降采样/模糊，再画到 canvas）
4. 复制 / 保存 / 固定 / 识别 都用这张合成后的 PNG
```

项目已依赖 SkiaSharp 3.119.4，OCR 插件也在用；不需要新包。

## 五、交互细节（易踩的坑）

| 坑 | 处理 |
|---|---|
| 标注与拖选区打架 | `DragMode` 增加 `Annotate`；有选中工具时鼠标事件先给工具 |
| 文字输入法 | 用真实 `TextBox` 浮在选区上，提交后转 `TextNote`；不要自绘文本 |
| 高 DPI | 图元存 DIP，导出时乘 `_scaling`；线宽用像素对齐（`.5` 偏移） |
| 马赛克格子 | 按**物理像素**算单元格大小，否则 150% 缩放下格子不一致 |
| 撤销后悬停态 | 重画时清掉命中缓存，避免抓到已删除的图元 |
| 工具条太宽 | 见第二节的折叠规则；7 个动作 + 6 个工具一定超宽 |

## 六、分期

| 期 | 内容 | 状态 |
|---|---|---|
| P1 | 数据模型 + Skia 合成 + 矩形/椭圆/箭头/画笔 + 撤销 + 工具条骨架 | ✅ 已完成 |
| P2 | 文字（含输入法）、马赛克、序号、高亮 | ✅ 已完成 |
| P3 | 属性折叠、更多菜单、快捷键（数字键切工具）、颜色/线宽选择、马赛克像素/模糊切换 | ✅ 除「工具条折叠」外已完成 |

### P1 实际落地（2026-11-20）

- `Views/Annotations.cs`：`ShapeAnnotation` / `ArrowAnnotation` / `PenAnnotation` + `AnnotationDocument`（撤销/重做，新图元清空重做栈）
- `Views/AnnotationRenderer.cs`：SkiaSharp 合成，按 `_scaling` 把 DIP 映射到物理像素
- 截图窗口：工具行（矩形/椭圆/箭头/画笔/撤销）+ 预览层（`NoteLayer`），画完的图进复制/保存/固定/识别
- 选区移动或缩放会清空标注（坐标相对选区，不清理必然错位）
- `Ctrl+Z` 撤销；点一下没拖动不进撤销栈
- 24 条用例：合成像素、缩放映射、箭头几何、撤销栈、无头窗口画图 + 撤销

**颜色/线宽暂时固定**（琥珀 #E5A44C、3px），颜色选择器放 P3——先把「能画、能导出」跑通。

### P2 实际落地（2026-11-21）

| 工具 | 实现 |
|---|---|
| 高亮 | `PenAnnotation` 加 `Opacity`，宽 18px、透明度 0.4 |
| 马赛克 | `MosaicAnnotation`：像素化 = 区域内缩小到 `cell` 网格再用最近邻放大；模糊模式渲染器已支持，界面暂未开放切换（P3） |
| 文字 | 选区内就地弹出 `TextBox`（真实输入法），Enter / 失焦提交，Esc 取消；转成 `TextAnnotation` 用 Skia 的 `DrawText` 导出 |
| 序号 | 点击放置，自动递增；圆形底 + 白色数字 |

马赛克的像素化用**线性采样**缩小（否则棋盘格缩完还是黑白），放大回原尺寸时用最近邻，块边界才干净。

**已知限制**：文字单行；马赛克固定像素模式；序号颜色跟随全局琥珀。

### P3 实际落地（2026-11-21）

| 项 | 实现 |
|---|---|
| 颜色 | 5 个色块（琥珀/白/红/绿/蓝），只影响之后画的图元；已有图元保持自己的颜色 |
| 粗细 | 2 / 4 / 8 三档 |
| 马赛克模式 | 工具栏「马赛克·像素 / 马赛克·模糊」切换，模糊走 Skia 的 `SKImageFilter.CreateBlur` |
| 数字键 | 2 矩形、3 椭圆、4 箭头、5 画笔、6 高亮、7 马赛克、8 文字、9 序号（和 ToolTip 一致） |
| 工具条折叠 | **未做**：三行（工具 / 样式 / 动作）在 1080p 下够用；需要时再按 snow-shot 的 compaction 做 |

预览颜色跟着图元自己的颜色走：换红色再画，新矩形是红的，旧的不变。

### 吸附参考线（2026-11-21，参考 snow-shot 的 `snapGuides`）

| 场景 | 吸附对象 |
|---|---|
| 拖动整个选区 | 屏幕左右边 / 水平中线、上下边 / 垂直中线（`SnapEdges`，左右边取更近的一条） |
| 拉伸选区 | 只吸附正在拖的那条边 |
| 画标注 | 选区的边框和中线（`SnapToBox`） |

- 阈值 6 DIP（`SnapGuides.Tolerance`）；命中时画琥珀虚线，松手立刻消失
- 纯计算在 `Views/SnapGuides.cs`，四条用例覆盖「阈值内/外、取更近边、标注落点、无头拖动出线」
- **未做**：吸附到检测到的窗口边缘（`_currentCandidate` 只在没有选区时跟踪），以及多显示器下按当前屏取中线（现在用整个虚拟桌面）

## 七、验收

- 画完 → 复制 / 保存 / 固定 / 识别，四个出口拿到的都是**带标注**的图
- 150% 与 100% 缩放下，箭头粗细与马赛克格子视觉一致
- 撤销 20 步不卡（每次重画是 O(n)，n 很小）
- 选区重设后标注清空（避免旧图元落在新选区外）
