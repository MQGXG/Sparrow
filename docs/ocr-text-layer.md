# OCR 文字层：参考实现分析与差距

对照对象：

| 参考 | 位置 | 源码 |
|---|---|---|
| LumaShot | 置顶贴图窗口（pin） | `D:\mycodeHub\LumaShot\src\pin\selection_tools.*`、`src/ocr/text.h` |
| snow-shot | 截图识别窗口 | `D:\mycodeHub\snow-shot\snow_shot` |

**两个参考都做了同一件事**：OCR 文字可以被拖选，选中后弹一个浮动菜单，
给选中的字加荧光笔 / 波浪线 / 下划线 / 删除线，**并且这些标记会烧进最终复制/保存的图片**。
差别在深度和位置，不在效果。

本文只谈**交互层**，不谈 OCR 引擎本身（引擎见 `ocr-engine-comparison.md`）。

---

## 一、参考实现做了什么

它是三层结构，缺一层都做不出截图里的效果。

### 1.1 行模型：不只是框，是带方向的四边形

`ScreenshotOcrLine`（`screenshotocrpresentation.h`）：

```cpp
QString text;  qreal confidence;
QPolygonF quad;                                   // 四边形，不是 AABB
ScreenshotOcrTextDirection direction;             // 横排 / 竖排
QVector<QPolygonF> sourceLineQuads;               // 同一行的**分词框**（含词间空隙）
bool paragraph;
QColor backgroundFillColor;                       // 从源像素采样
std::optional<qreal> fontSizeCapRatio;            // 字号上限 = 有效字号 / 短边
QColor sourceTextColor;                           // 从原图采样的文字色
```

关键在 `sourceLineQuads`：有**词框**才能算字符位置。`sourceTextColor` 是从原图像素反推的，
所以把识别文字画上去时颜色和原图一致，不会突然变成黑色。

### 1.2 选择：字符级 anchor / focus

```cpp
struct ScreenshotOcrTextPosition { int lineIndex; int characterIndex; };

void beginTextSelection(const ScreenshotOcrTextPosition&);
void updateTextSelection(const ScreenshotOcrTextPosition&);
void finishTextSelection();
ScreenshotOcrTextRange textSelectionForLine(int lineIndex) const;  // {start, length}
QString selectedText() const;
```

- 选区是**跨行的字符区间**，不是像素矩形。
- 另有 `beginLineBoxSelection`：拖矩形按行整行选中（两种模式并存）。
- 每次变更递增 `selectionRevision`，渲染侧按 revision 增量重绘。

这正是截图里蓝色圆形手柄能贴着「机械键盘 K8」首尾的原因——手柄位置由字符位置算出来，不是框的四角。

### 1.3 浮动菜单：复制 + 四种文字标注

`ScreenshotQuickTextAnnotationToolbar`（`screenshotquicktextannotationtoolbar.cpp`）：

```
复制 | 荧光笔 | 波浪线 | 下划线 | 删除线
```

- 「复制」= `copyCommittedSelectionFrom(...)`，只复制选中的那段。
- 其余四个是**文字标注工具**，对应 `ScreenshotTextAnnotationTool` 枚举。

### 1.4 标注：以「字符区间」为键的 run

`ScreenshotTextAnnotationDocument`（`screenshottextannotation.h`）：

```cpp
enum class ScreenshotTextAnnotationTool { Highlight, WavyUnderline, Underline, Strikethrough };

struct ScreenshotTextSelectionRange { int sourceLine; int start; int end; };

struct ScreenshotTextAnnotationRun {
    ScreenshotTextSelectionRange range;
    std::optional<ScreenshotTextAnnotationStyle> highlight;  // 底色
    std::optional<ScreenshotTextAnnotationLine>  line;       // 线（tool + style）
};

struct ScreenshotTextAnnotationStyle { QColor color = #F5222D; qreal width = 2.0; ... };
```

三个设计点：

1. **toggle 语义**：同一工具再点一次就取消（`toggle()`），和加粗按钮一样。
2. **绑定源文本**：`bindSource(source, fingerprint)` + `validForSource(lengths)`；
   重新识别后文本变了，旧标注自动失效，不会错位到别的字上。
3. **可序列化**：`serialize()` / `restore()`，能跟着工程/历史存下来。

默认颜色 `#F5222D`（红），截图里的波浪线就是它。

### 1.5 渲染

文字是**画在图上**的（`prepareForRendering()` 按 quad 拟合字号），
标注也画在文字下面，所以导出时波浪线正好压在字的下沿，而不是浮在空白处。

### 1.6 LumaShot：同一效果的轻量版

LumaShot 走的是另一条路，代码量小得多（`pin/selection_tools.h` 只有 17 行声明）。

**行模型**（`ocr/text.h`）比 snow-shot 更细：

```cpp
struct Glyph { std::wstring text; Box box; float confidence; };   // ← 逐字框
struct Line  { std::vector<Glyph> glyphs; Box box; std::array<Point,4> quad; };
struct Position { size_t line, glyph; };                          // 字形边界，不是 UTF-16 码元
struct Selection { Position anchor, caret; };
```

它从 CTC 解码（`DecodeCtc`）里直接拿到**每个字的框**，所以选择粒度是「字」，不是「词」。

**选择与菜单**：

```cpp
std::vector<Box> SelectionBoxes(const ocr::Text&, ocr::Selection);  // 每行合并成一个 box
SelectionHandles TextHandles(...);   // {左上角上移 5px, 右下角下移 5px}
int HitTextHandle(...);              // 命中半径 10px
SelectionBar PlaceSelectionBar(...); // 菜单 400×80（按 scale 缩），在选区下方 18px，越界翻上方
```

**四种标记 + 烧进图片**：

```cpp
enum class TextDecoration { Highlight, Wave, Underline, Strike };
struct SelectionMark { TextDecoration kind; std::vector<Box> boxes; };
Frame FlattenTextMarks(const Frame&, const std::vector<SelectionMark>&);  // 复制/保存时调用
```

具体像素规格（`DrawTextMarks`）：

| 标记 | 颜色 | 画法 |
|---|---|---|
| 荧光笔 | `#FFDF42` α0.4 | 填充 box |
| 波浪线 | `#F43F67` | 沿底边画正弦折线，`y + sin(x*0.7)*stroke` |
| 下划线 | `#183451` | 底边 -1px 直线 |
| 删除线 | `#183451` | 中线直线 |

线宽 = `clamp(行高/14, 1.5, 3)`，跟着字号自动变。

**位置差别**：LumaShot 的这套交互在**置顶贴图窗口**（pin）里，不在截图选区上；
snow-shot 在**截图识别窗口**里。Sparrow 要放哪边，见第五节。

### 1.7 两者对比

| 维度 | LumaShot | snow-shot |
|---|---|---|
| 位置 | pin 窗口 | 截图识别窗口 |
| 位置粒度 | **逐字框**（CTC） | 词框 + quad |
| 选择模型 | `anchor/caret (line, glyph)` | `anchor/focus (line, char)` + 框选整行 |
| 菜单 | 400×80 定长条，5 键 | 自适应工具条，5 键 |
| 手柄 | 两个点（左上 -5 / 右下 +5） | 两个点 |
| 标记存储 | `vector<SelectionMark>` + undo 栈 | run 文档 + fingerprint + 序列化 |
| 默认色 | 荧光 `#FFDF42`／波 `#F43F67`／线 `#183451` | 统一 `#F5222D`，可配色 |
| 落地方式 | `FlattenTextMarks` 烧进图 | 导出时按 run 绘制 |
| 翻译叠层 | 有（`selection_translator`） | 有 |

对 Sparrow 来说，**LumaShot 的数值可以直接抄**（颜色、线宽公式、手柄偏移、菜单尺寸）；
snow-shot 的**文档模型**（fingerprint / toggle / 序列化）值得借鉴，但不必一次做全。

---

## 二、Sparrow 现在是什么

| 项 | 现状 |
|---|---|
| 行模型 | `OcrTextLine(string Text, double X, double Y, double Width, double Height)` —— **只有 AABB，没有词框、没有 quad、没有方向** |
| 叠层 | `OcrLayer` 里每行一个**透明矩形**（1px 描边、圆角 2、手型光标） |
| 悬停 | 高亮填充 |
| 点击 | 复制**整行** |
| 全文 | 工具条「复制全部」/ `Ctrl+C`；格式 ▾ / 标点 ▾ 只影响复制出去的文本 |
| 显示模式 | `B` 切换 全部 / 悬停 / 关闭 |
| 文字渲染 | **不画文字**，只画框 |
| 标注 | 无 |
| 导出 | 标注与 OCR 无关，OCR 结果不进图片 |

也就是说：**Sparrow 的 OCR 是「框 + 一个文本面板」，参考是「图上的一层可选文字」。**

> **2026-11-23 更新**：行框已经改成「常显红框 + 多选 + 阅读顺序合并」，见 §2.1。
> 字符级选择和文字标记仍未做。

### 2.1 已落地的部分（2026-11-23）

| 能力 | 现在的行为 |
|---|---|
| 行框视觉 | 常态 `#FF5F5F` 1px + 12% 红底；悬停 2px + 20% 底；**选中琥珀 2px + 12% 琥珀底** |
| 默认显示 | 改成**全部显示**（原来默认「悬停」），识别完就能看到每块文字在哪 |
| 点击 | **切换选中**，可多选，不用按 Ctrl |
| 双击 | 复制这一行（保留原来的肌肉记忆） |
| Ctrl+C / 复制按钮 | 有选中 → 只复制选中的行（按阅读顺序合并）；无选中 → 复制全文 |
| 状态文字 | `3 行 · 已选 1 · Ctrl+C 复制` |
| `B` 键 | 仍可切 全部 / 悬停 / 关闭 |
| **字符级选择** | 在行框上拖动按字符选（`OcrTextLayout` 近似定位），选区底色琥珀 25% |
| **浮动菜单** | 选区下方（放不下翻上方）：复制 / 荧光笔 / 波浪线 / 下划线 / 删除线 |
| **标记** | 同类型同区间再点一次取消；荧光笔琥珀 35%，波浪线红，其余琥珀 |
| **标记烧进图** | `ComposeResult` 把标记交给 `AnnotationRenderer.Compose`，导出 PNG 里能看到 |

合并规则（`CaptureWindow.MergeOcrText`）：先按 Y 聚成行（容差 = 行高一半），
行内按 X 排序；**两侧都是西文才补空格**，中文之间直接相接。

这部分的取值参考了 OmniSeeker（红框 + 多选 + 阅读顺序），但配色保留了 Sparrow 的琥珀强调，
没有引入它的蓝色选中态。

### 2.2 译文叠层（2026-11-23）

翻译不再只出文本，而是把译文**画回截图**，并且能在原位切回原文。

| 项 | 行为 |
|---|---|
| 触发 | 识别 + 选目标语言（`TranslateButton` 的菜单） |
| 粒度 | **表格选区按格翻译**：Paddle 本来就每格一个 region，检测到表格形状就保留 region 粒度（`LooksLikeTable` / `ComposeRegionLines`）；普通文本仍按阅读行 |
| 翻译输入 | 把行框文本按行拼起来（一格一行）送给模型，提示词要求「输入几行输出几行」，避免模型重排成「字段: 值」 |
| 数据 | 插件把译文按行回填到 `OcrTextLine.Translation`（行数对不上就不回填） |
| 画面 | `TransLayer` 盖在选区上：逐格用原图底色盖掉原文，再写译文 |
| 字号 | 上限取行框短边的 82%，超宽自动折行；放不下先缩字号，最小 6px，仍放不下才裁剪，绝不越出格子 |
| 排版 | 英文在空格处断行、中文按字符断；左对齐 + 垂直居中 |
| 文字色 | 按底色亮度反选（亮底黑字、暗底白字） |
| 切换 | **独立译文行**（主工具条下面）：`显示原图 | 译文 · en | 对比 | 复制译文 | 关闭` |
| 对比 | 原图与译文图左右并排，按窗口可用区域等比放大并居中到选区，不再塞进选区被压扁 |
| 引擎 | 独立「引擎」按钮（图标）：自动（在线优先）/ 在线模型 / 已下载的本地模型，来自宿主能力列表；只列真实可用的，只有一个选项时按钮隐藏 |
| 导出 | 当前是译文视图就以译文图为底再叠标注，看到什么导出什么 |

比较效果时仍可回到原文（眼睛按钮），不需要重新识别。
按行对不上时**不画译文**，状态栏提示「译文未能按行对齐，已保留原文」——宁可只显示原文，也不整段盖住内容。

**未做**：译文与原文的逐句悬停对照。

---

## 三、逐项差距

| 能力 | 参考 | Sparrow | 差距 |
|---|---|---|---|
| 识别文字画在图上 | ✓（拟合字号 + 采样颜色） | ✗ | **大** |
| 字符级选择 | ✓（anchor/focus） | ✓ 拖动选字（位置是近似，误差 ≤ 1 字） | 小 |
| 框选整行 | ✓ | 点行框切换整行选中 | 小 |
| 选区手柄 | ✓（贴字符） | ✗ | 中 |
| 复制选中片段 | ✓ | ✓ 字符级 / 整行多选都支持 | — |
| 荧光笔（底色） | ✓ | ✓ | — |
| 波浪线 / 下划线 / 删除线 | ✓（可配色/线宽） | ✓（固定色，线宽随行高） | 小 |
| 标注与文本绑定 | ✓（fingerprint 失效） | ✗（结果变了就整体清空） | 小 |
| 标注可序列化 | ✓ | ✗（只在本次会话内） | 中 |
| 翻译叠层 | ✓（同一行模型） | 结果在卡片里 | 中 |
| 竖排文字 | ✓ | ✗ | 小 |

---

## 四、为什么参考能这么做，Sparrow 缺的前提

1. **词框**：字符位置靠 `sourceLineQuads` 累加宽度得到。Sparrow 的 `OcrTextLine` 没有词框，
   只能按「行宽 ÷ 字符数」近似。
2. **文字是渲染出来的**：所以「选中的是文字」而不是「选中的是框」。
   Sparrow 不画文字，即使做了选区，视觉上也只有空框。
3. **标注存的是字符区间**：所以文本重排/重新识别后可以判定失效。
   Sparrow 连字符位置都没有，无从谈起。

---

## 五、Sparrow 要补的能力（分期建议）

### P1 · 字符定位 ✅ 已落地（宿主近似）

**先说结论**：Paddle 这条链路拿不到字框。

- worker 里 `result.Regions` 只有「文本行」的 `Rect`，`OcrRegionDto` 也只有这一个框；
- LumaShot 的逐字框来自它自己的 CTC 解码（`DecodeCtc` 直接返回 `Line{glyphs}`），Sparrow 复制不了。

所以没有改 SDK 契约，而是在宿主侧做近似：`src/Sparrow.App/Views/OcrTextLayout.cs`

```csharp
IReadOnlyList<OcrCharBox> Split(OcrTextLine line);   // 行 → 逐字框，宽度之和 = 行宽
int HitIndex(IReadOnlyList<OcrCharBox> boxes, Point point);
double Weight(Rune rune);                            // 中日韩 1.0 / 西文 0.55 / 空格 0.3
```

- 按**字素**切分（代理对算一个字，emoji 不会被选半个）；
- 最后一个字符收到行右边界，消掉浮点累积误差；
- 误差上限是「一个字宽」，且同一行内相对顺序是对的。

测试：`tests/Sparrow.Tests/OcrTextLayoutTests.cs`（6 条：纯中文 / 纯西文 / 中英混排 / 非法输入 / 代理对 / 命中）。

**如果将来换引擎能出字框**：给 `OcrTextLine` 加可选的 `Glyphs`，`OcrTextLayout` 优先用真实框，
没有时再走近似——接口已经按这个形状留好了。

### P2 · 文字层渲染（可选，但强烈建议先做）

- 在 `OcrLayer` 里给每行加一个 `TextBlock`：字号按行高拟合，颜色取源图采样（或默认深灰）。
- 做成「可开关」：`B` 模式里加一档「文字层」。

**验收**：文字与行框对齐、横排竖排都正确；关掉后回到现在的框模式。

### P3 · 选择 + 浮动菜单 ✅ 已落地（2026-11-23）

- 字符级 anchor/focus：在行框上拖动，用 `OcrTextLayout.IndexAt` 定位到字符边界。
- 单击行框仍是「整行多选」，双击复制整行；拖动超过 4 DIP 才进入字符选择，两者不打架。
- 浮动菜单：复制 / 荧光笔 / 波浪线 / 下划线 / 删除线，摆在选区下方（放不下翻上方）。
- 复制只复制选中片段（跨行时按行拼）。

**未做**：起止手柄（LumaShot 的两个圆点）、框选整行、Shift 扩选。

### P4 · 标注 + 导出 ✅ 已落地（2026-11-23）

```csharp
internal sealed record OcrTextMark(TextMarkKind Kind, IReadOnlyList<Rect> Boxes, uint Color, double Width);
```

- 标记存「类型 + 框集合」（框是选区内 DIP），同一类型同一组框再点一次就取消。
- 预览画在 `OcrLayer` 上（`Tag = "ocr-mark"`），导出由 `AnnotationRenderer.Compose(..., textMarks)` 画进 PNG。
- 颜色：荧光笔琥珀 35%，波浪线红 `#FF5F5F`，下划线/删除线琥珀；线宽 `clamp(行高/14, 1.5, 3)`。
- 波浪线用正弦折线（和 LumaShot 的 `sin(x*0.7)` 同参数）。

**未做**：标记颜色/线宽可配、序列化、文本变更后的指纹失效（现在换选区/重识别就整体清空）。

**验收**：导出图上波浪线压在字下沿 ✅；再点一次「波浪线」取消 ✅。

### P5 · 翻译叠层（复用 P1 的行模型）

- 译文按行替换或叠加，沿用 `fontSizeCapRatio` 的思路限字号。

---

## 六、顺序与风险

| 期 | 依赖 | 风险 |
|---|---|---|
| P1 ✅ | 插件返回词框 | 低（纯数据） |
| P2 | P1 | 中（字号拟合、颜色采样） |
| P3 ✅ | P1 | 中（选择状态机） |
| P4 ✅ | P3 | 中（导出坐标） |
| P5 | P1 | 中（排版） |

**最大的风险在 P1**：如果 OCR 插件拿不到词框，字符级选择就只能是近似，
拖选 3 个字可能选中 2.7 个字的宽度。建议先确认 `sparrow.plugin.ocr` 的 Paddle 结果里
是否有 `word_boxes`——有就直接透传，没有就退回「按行 + 字宽近似」并把误差写进文档。

> **已确认（2026-11-23）**：Paddle 链路只有行框。已按「宿主近似」落地 `OcrTextLayout`，
> 误差上限一个字宽。见第五节 P1。

---

## 七、明确不做（本轮）

- 竖排文字的完整支持（先按横排做，竖排只保证不错位）。
- 文字的富文本编辑（参考也只做标记，不做改字）。
- 把 OCR 文字层做成插件能力（它依赖截图会话的叠层，和取色同理，留在宿主）。
