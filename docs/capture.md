# 截图选区与工具条

> 参考了 snow-shot 的 `anchoredToolbarPlacement` 与工具条结构，按 Sparrow 的深灰 + 琥珀配色重画。

## 一、动作

| 按钮 | 快捷键 | 行为 |
|---|---|---|
| 识别文字 | Enter / R | OCR 选图，结果以行框叠在选区上，可点一行复制一行 |
| **翻译 ▾** | — | 点开选目标语言（来自 OCR 工具的 12 种），识别后附译文 |
| 复制图片 | C | 选区 PNG 进剪贴板 |
| 保存 | S | 另存 PNG |
| 固定 | P | 贴到屏幕上（滚轮缩放） |
| 滚动 | G | 进入滚动截图 |
| 取消 | Esc | 关闭 |

「翻译」的语言列表**不写死在截图窗口里**：`App.TranslateTargets()` 直接读 OCR 工具
`translate` 参数的 `Choices`，跳过「不翻译」。插件加语言，截图工具条自动跟着变。

## 二、翻译流程

```
拖出选区
  → 点「翻译 ▾」选 English
  → CaptureSelectionAsync 按选区取 PNG（有冻结帧就裁冻结帧，不再抓屏）
  → OcrRunner(png, CaptureOcrOptions("English"), ct)
  → 宿主把 parameters["translate"] = "English" 交给 OCR 工具
  → 在线 AI 优先；没配 AI 时用已下载的本地模型
  → 行框 + 结果卡片（原文 + 译文）
```

结果卡片是 `SelectableTextBlock`，可以选中局部复制；「复制」按整段复制。

## 三、工具条放置（学自 snow-shot）

```
首选：右边缘对齐选区右边缘，放在选区下方，间距 8
放不下：翻到选区上方
最后：横向 / 纵向钳制在窗口内，边距 8
```

关键点是**用实测尺寸**（`ActionBar.Measure` → `DesiredSize`）而不是估算宽度：
按钮数量一变，估算就失效（之前写的是 `estimatedWidth = 620`）。

纯函数在 `CaptureWindow.ComputeToolbarPosition`，用例覆盖「翻上方」「贴边钳制」。

结果卡片另有一套：优先选区右侧，放不下换左侧，再钳制（`ComputeCardPosition`）。

## 四、和 snow-shot 的差异

| snow-shot | Sparrow |
|---|---|
| 工具条可拖动（拖动把手） | 未做：位置跟随选区，避免多一个需要理解的状态 |
| 按屏幕尺寸自动紧凑（layout compaction） | 未做：动作少，暂时不需要 |
| 工具分组 + 分隔线 | 未做：7 个按钮还没到需要分组的程度 |
| 选区尺寸 / 单位切换 / 圆角 / 边框 | 只有尺寸角标（物理像素），其余未做 |

如果以后动作超过 10 个，再考虑把「输出类」（复制/保存/固定）和「输入类」（识别/翻译）分组，
并参考它的紧凑规则。
