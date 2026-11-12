# 工具清单（Tool Catalog）

> **这份文档是工具集合的唯一事实来源。** 新增、改名、改状态，只改这里；其他文档引用它，不复制它。

工具集合不是一张平铺的列表，而是一张**输入 × 输出矩阵**。矩阵决定了哪些工具能互相接力，也暴露了集合里缺什么。

---

## 一、工具的组织模型

一个工具 = 一个函数，签名固定：

```text
Tool : InputShape × Params → OutputShape
```

**任何工具都不得引入第四种要素**（不改窗口、不改主题、不注册快捷键、不直接读写数据库）。这保证「加工具不改壳」。

### 1.1 输入形态

| 形态 | 含义 | 典型来源 |
|---|---|---|
| `Text` | 纯文本 / 代码 | 剪贴板、选区、手输、上一个工具 |
| `RichText` | 带格式文本（HTML / RTF） | 剪贴板 |
| `Image` | 位图 | 剪贴板、截图、文件拖入 |
| `Files` | 一个或多个路径 | 拖入、文件选择器、剪贴板文件 |
| `Selection` | 当前前台应用选中的文本 | UI Automation |
| `Nothing` | 无输入（生成类工具） | 手输参数 |

### 1.2 输出形态

| 形态 | 含义 | 可被谁消费 |
|---|---|---|
| `Text` | 纯文本 / 代码 | 所有 `Text` 输入工具、剪贴板、保存 |
| `Svg` | SVG 源码 | SVG 预览、SVG→PNG、保存 |
| `Image` | 位图 | 图片工具、OCR、保存 |
| `FileSet` | 一组文件 | 文件工具、打开所在位置 |
| `Table` | 二维表格 | 导出 CSV、复制为 TSV |

### 1.3 矩阵（谁接谁）

行 = 输出，列 = 输入。`✅` = 可直接接力。

| 输出 ↓ \ 输入 → | Text | RichText | Image | Files | Selection |
|---|:--:|:--:|:--:|:--:|:--:|
| **Text** | ✅ | | | | |
| **Svg** | ✅ | | | | |
| **Image** | | | | | |
| **FileSet** | ✅ | | | | |
| **Table** | ✅ | | | | |

读法：`Svg` 输出可以喂给任何吃 `Text` 的工具（因为 SVG 源码就是文本），但不能直接喂给吃 `Image` 的工具——必须先经 `svg → png` 转换。**这条约束让接力链不会出现隐式转换。**

### 1.4 每个工具 = 一个插件

本表里的每一行，**在工程上都是一个独立的插件目录**：

```text
工具 ID   sparrow.json.format
插件 ID   sparrow.plugin.json-format        ← 由工具 ID 机械推导
目录       plugins/sparrow.plugin.json-format/
```

推导规则：`sparrow.<分类>.<动作>` → `sparrow.plugin.<分类>-<动作>`（点换横线，节点头加 `plugin`）。

因此本表的每一行都自动获得：

```text
可以单独禁用      settings.json 的 disabledPlugins
可以单独删除      删目录即可，宿主零改动
可以单独升级      各自的依赖互不影响
```

**例外的只有一种**：一个插件声明多个工具（`plugin.json` 的 `tools` 数组有多项）。目前只有 OCR（`region` + `image` 共用同一个 Paddle 模型）会这样。

插件机制细节见 [`plugin-model.md`](plugin-model.md)。

---

## 二、状态图例

| 标记 | 含义 |
|---|---|
| `P0` | 本期必做，进入 M0–M1 |
| `P1` | 下期，M2–M3 |
| `P2` | 候选池，未排期 |
| `✂` | 已评审砍掉，保留记录防止重复讨论 |
| `✅` | 已实现 |
| `🚧` | 实现中 |

---

## 三、工具总表

### 3.1 剪贴板 · Clipboard

| ID | 名称 | 输入 | 输出 | 阶段 | 状态 | 备注 |
|---|---|---|:--:|:--:|---|---|
| `sparrow.clipboard.history` | 剪贴板历史 | Nothing | Text / Image / Files | **P0** | ⬜ | 列表 + 搜索 + 固定 + 去重 |
| `sparrow.clipboard.snippet` | 固定片段 | Nothing | Text | P1 | ⬜ | 长期保留的常用文本 |
| `sparrow.clipboard.merge` | 合并多段 | Nothing | Text | P2 | ⬜ | 多选历史按序拼接 |
| `sparrow.clipboard.sync` | 跨设备同步 | — | — | ✂ | — | 违反本地优先 |

### 3.2 文本 · Text

| ID | 名称 | 输入 | 输出 | 阶段 | 状态 | 备注 |
|---|---|---|:--:|:--:|---|---|
| `sparrow.text.transform` | 文本变形 | Text | Text | P1 | ⬜ | 大小写 / 去空行 / 去重 / 排序 / 反转 |
| `sparrow.text.replace` | 批量替换 | Text | Text | P1 | ⬜ | 支持正则、支持多组规则 |
| `sparrow.text.extract` | 正则提取 | Text | Text / Table | P1 | ⬜ | 提取分组为行 |
| `sparrow.text.count` | 字数统计 | Text | Table | P2 | ⬜ | 字符 / 词 / 行 / 中文字数 |
| `sparrow.text.script` | 文本脚本 | — | — | ✂ | — | 要沙箱，成本远超收益 |

### 3.3 JSON · Json

| ID | 名称 | 输入 | 输出 | 阶段 | 状态 | 备注 |
|---|---|---|:--:|:--:|---|---|
| `sparrow.json.format` | JSON 格式化 | Text | Text | **P0** | ⬜ | mode: 格式化 / 压缩 / 校验；错误定位到行列 |
| `sparrow.json.toCode` | 转 C# 类 | Text | Text | P1 | ⬜ | 生成 record / class |
| `sparrow.json.query` | JSONPath 查询 | Text | Text | P1 | ⬜ | 路径输入 + 结果高亮 |
| `sparrow.json.diff` | JSON 对比 | Text | Table | P2 | ⬜ | 需要三栏 UI，成本高 |
| `sparrow.json.yaml` | JSON ↔ YAML | Text | Text | P2 | ⬜ | 先看 M1 后是否有真实需求 |

### 3.4 文件 · Files

| ID | 名称 | 输入 | 输出 | 阶段 | 状态 | 备注 |
|---|---|---|:--:|:--:|---|---|
| `sparrow.files.rename` | 批量重命名 | Files | FileSet | P1 | ⬜ | 预览 + 冲突检测 + 撤销 |
| `sparrow.files.hash` | 文件哈希 | Files | Table | P1 | ⬜ | MD5 / SHA1 / SHA256 |
| `sparrow.files.path` | 路径处理 | Text | Text | P1 | ⬜ | 转斜杠 / 提取文件名 / 拼接 |
| `sparrow.files.duplicate` | 重复文件查找 | Files | Table | P2 | ⬜ | 需要索引，收益不确定 |

### 3.5 图片 · Images

| ID | 名称 | 输入 | 输出 | 阶段 | 状态 | 备注 |
|---|---|---|:--:|:--:|---|---|
| `sparrow.images.convert` | 格式转换 | Image | Files | P1 | ⬜ | PNG / JPEG / WebP |
| `sparrow.images.resize` | 缩放 | Image | Image | P1 | ⬜ | 按边长 / 按比例 / 目标体积 |
| `sparrow.images.pickColor` | 取色 | Image | Text | P1 | ⬜ | 拾色器 + HEX / RGB |
| `sparrow.images.qr` | 二维码 | Text | Image | P2 | ⬜ | 生成 + 识别 |
| `sparrow.images.batch` | 批量处理 | — | — | ✂ | — | 是另一个产品 |

### 3.6 文字识别 · OCR

| ID | 名称 | 输入 | 输出 | 阶段 | 状态 | 备注 |
|---|---|---|:--:|:--:|---|---|
| `sparrow.ocr.region` | 区域识别 | Nothing | Text | **P0** | ⬜ | 圈选 → 识别 → 可接力 |
| `sparrow.ocr.image` | 图片识别 | Image | Text | **P0** | ⬜ | 与 region 共用引擎，只是输入不同 |
| `sparrow.ocr.table` | 表格识别 | Image | Table | P2 | ⬜ | 引擎支持有限，先不承诺 |

> **OCR 是唯一进独立进程的工具**（Paddle 原生约 78MB + OpenCV 38MB）。主进程不加载它，首次使用时启动子进程，用完退出。见 [`architecture.md`](architecture.md#ocr-为什么独立进程)。

### 3.7 SVG

| ID | 名称 | 输入 | 输出 | 阶段 | 状态 | 备注 |
|---|---|---|:--:|:--:|---|---|
| `sparrow.svg.trace` | 图片描摹 | Image | Svg | P1 | ⬜ | 千万色 → 调色板量化 → 路径拟合 |
| `sparrow.svg.preview` | SVG 预览/导出 | Svg | Image / Files | P1 | ⬜ | 走 Svg.Skia 5.1.1 |
| `sparrow.svg.optimize` | SVG 优化 | Svg | Svg | P2 | ⬜ | 去冗余属性 / 合并路径 |
| `sparrow.svg.generate` | AI 生成 SVG | Text | Svg | P2 | ⬜ | 依赖 AI，绝不能前置 |

### 3.8 开发 · Dev

低成本工具，用来验证「加工具不改壳」。除 P0 外全部 M1。

| ID | 名称 | 输入 | 输出 | 阶段 | 状态 |
|---|---|---|:--:|:--:|---|
| `sparrow.dev.base64` | Base64 编解码 | Text | Text | P1 | ⬜ |
| `sparrow.dev.url` | URL 编解码 | Text | Text | P1 | ⬜ |
| `sparrow.dev.timestamp` | 时间戳转换 | Text | Text | P1 | ⬜ |
| `sparrow.dev.guid` | GUID 生成/解析 | Nothing | Text | P1 | ⬜ |
| `sparrow.dev.jwt` | JWT 解析 | Text | Text | P1 | ⬜ |
| `sparrow.dev.hash` | 文本哈希 | Text | Text | P1 | ⬜ |

### 3.9 AI（可选增强）

**全部 P2 起。不配置任何模型时，这些工具在面板里置灰并给出配置入口。**

| ID | 名称 | 输入 | 输出 | 阶段 | 状态 |
|---|---|---|:--:|:--:|---|
| `sparrow.ai.translate` | 翻译 | Text | Text | P2 | ⬜ |
| `sparrow.ai.explain` | 解释代码/报错 | Text | Text | P2 | ⬜ |
| `sparrow.ai.summarize` | 总结 | Text | Text | P2 | ⬜ |
| `sparrow.ai.redraw` | 视觉重绘 | Image | Svg | P2 | ⬜ |

---

## 四、集合的缺口分析

矩阵不是装饰，它直接告诉你下一步该补什么。

| 观察 | 结论 |
|---|---|
| `Image` 输出没有工具消费 | **M2 必须补一个 Image 输入工具**（resize 或 convert），否则截图类链路是断头路 |
| `RichText` 输入无人使用 | 剪贴板历史只存纯文本，是有意的简化；RichText 等真实需求出现再做 |
| `Table` 只有输出没有输入 | 目前只用于「导出 CSV」；等 Table 输入工具达到 2 个再抽象 |
| `Files` 输入侧工具偏少 | 文件域是 P1 洼地，但优先级低于 SVG（SVG 是产品起源） |
| `Selection` 平台差异大 | 只有 Windows 能稳定拿前台选区；macOS 需要辅助功能授权 |

### 4.1 接力链（已验证会成立的组合）

```text
截图 → OCR → 文本 → JSON 格式化 → 复制
截图 → OCR → 文本 → 翻译(AI 开) → 复制
图片 → SVG 描摹 → SVG 预览 → 导出 PNG
剪贴板历史 → 文本 → 文本变形 → 复制回去
```

每条链都满足矩阵约束：**相邻两步的输出形态 ⊆ 下一步的输入形态**，无需隐式转换。

### 4.2 接力环路的防护

理论上可以构造 `OCR → 翻译 → OCR`（因为都是 Text）。为防止无限套娃：

```text
每个 ToolContext 带 TraceId + Depth
Depth > 3 时禁止「发送到其他工具」，只保留复制/保存
```

细节见 [`context-model.md`](context-model.md#流转与环路保护)。

---

## 五、维护规则

1. **新增工具先加到这里**（哪怕还没写代码），标记 `P2`。表里有记录，才不会重复讨论。
2. **ID 一旦进入 P0 就不能改**。它会出现在设置、历史库、配置里。
3. **ID 格式固定**：`sparrow.<分类>.<动作>`，全小写，动作用单个词，不加版本后缀。
4. **每个工具必须有唯一的输入形态和输出形态**。写不出这两列，说明它还没想清楚。
5. **砍掉的工具不删除，标 `✂`**，附一句原因。防止半年后又有人提。
