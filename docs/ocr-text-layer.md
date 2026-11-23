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
| 全文 | `ResultCard` 里一段可选中文本 + 「复制」；工具条上「复制全部」 |
| 显示模式 | `B` 切换 全部 / 悬停 / 关闭 |
| 文字渲染 | **不画文字**，只画框 |
| 标注 | 无 |
| 导出 | 标注与 OCR 无关，OCR 结果不进图片 |

也就是说：**Sparrow 的 OCR 是「框 + 一个文本面板」，参考是「图上的一层可选文字」。**

---

## 三、逐项差距

| 能力 | 参考 | Sparrow | 差距 |
|---|---|---|---|
| 识别文字画在图上 | ✓（拟合字号 + 采样颜色） | ✗ | **大** |
| 字符级选择 | ✓（anchor/focus） | ✗ | **大** |
| 框选整行 | ✓ | ✗ | 中 |
| 选区手柄 | ✓（贴字符） | ✗ | 中 |
| 复制选中片段 | ✓ | 只能整行 / 全文 | 中 |
| 荧光笔（底色） | ✓ | ✗ | 中 |
| 波浪线 / 下划线 / 删除线 | ✓（可配色/线宽） | ✗ | 中 |
| 标注与文本绑定 | ✓（fingerprint 失效） | — | — |
| 标注可序列化 | ✓ | — | — |
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

### P1 · 行模型升级（不动 UI）

- `OcrTextLine` 增加可选字段：`Glyphs`（每个字的 X/Y/W/H）+ `Direction`（横/竖）。
  **目标是逐字框**——LumaShot 的 CTC 解码本来就能出，snow-shot 至少能出词框。
- 插件侧：PaddleOCR 的结果里如果有字级/词级框，直接透传；只有行框时退回近似。
- 兼容：老插件不返回字框时，按「行宽 ÷ 字符数」近似（CJK 1em、西文 0.55em）。

**验收**：同一张图，字框数量与 OCR 调试输出一致；无字框时不崩。

### P2 · 文字层渲染（可选，但强烈建议先做）

- 在 `OcrLayer` 里给每行加一个 `TextBlock`：字号按行高拟合，颜色取源图采样（或默认深灰）。
- 做成「可开关」：`B` 模式里加一档「文字层」。

**验收**：文字与行框对齐、横排竖排都正确；关掉后回到现在的框模式。

### P3 · 选择 + 浮动菜单

- 字符级 anchor/focus（有词框用词框，没有就按字宽近似）。
- 选区手柄：起点/终点各一个圆点（贴字符）。
- 浮动菜单：复制 / 荧光笔 / 波浪线 / 下划线 / 删除线。
- 复制只复制选中片段。

**验收**：拖选一行中的 3 个字 → 复制得到这 3 个字；再点一次「波浪线」取消。

### P4 · 标注文档 + 导出

- `TextMarkupRun { lineIndex, start, length, tool, color, width }`，toggle 语义。
- 绑定识别结果指纹：文本变了就整体失效。
- 导出：在 `AnnotationRenderer` 里按 run 画底色/线，坐标由词框或字宽近似得出。

**验收**：导出图上波浪线压在字下沿；改选后重新识别，旧标注不残留。

### P5 · 翻译叠层（复用 P1 的行模型）

- 译文按行替换或叠加，沿用 `fontSizeCapRatio` 的思路限字号。

---

## 六、顺序与风险

| 期 | 依赖 | 风险 |
|---|---|---|
| P1 | 插件返回词框 | 低（纯数据） |
| P2 | P1 | 中（字号拟合、颜色采样） |
| P3 | P1 | 中（选择状态机） |
| P4 | P3 | 中（导出坐标） |
| P5 | P1 | 中（排版） |

**最大的风险在 P1**：如果 OCR 插件拿不到词框，字符级选择就只能是近似，
拖选 3 个字可能选中 2.7 个字的宽度。建议先确认 `sparrow.plugin.ocr` 的 Paddle 结果里
是否有 `word_boxes`——有就直接透传，没有就退回「按行 + 字宽近似」并把误差写进文档。

---

## 七、明确不做（本轮）

- 竖排文字的完整支持（先按横排做，竖排只保证不错位）。
- 文字的富文本编辑（参考也只做标记，不做改字）。
- 把 OCR 文字层做成插件能力（它依赖截图会话的叠层，和取色同理，留在宿主）。
