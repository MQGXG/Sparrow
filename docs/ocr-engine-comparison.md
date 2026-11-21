# OCR 引擎对比（M1 验证）

> 同一张 1280×720 图片、同一台机器、Debug 构建。
> 验证项目：`tools/RapidOcrProbe`（临时探针，协议与 `Sparrow.Ocr.Worker` 一致）。

## 数据

| | Sdcb.PaddleOCR（PP-OCRv5） | RapidOcrNet（PP-OCRv5 ONNX） |
|---|---|---|
| 输出目录体积 | **499.4 MB** | **56.8 MB** |
| 峰值内存 | 369.5 MB | 223.7 MB |
| 单张耗时 | 2.1–2.7 s | **1.19 s** |
| 原生依赖 | PaddleInference + MKL + OpenCV | ONNX Runtime + SkiaSharp |
| 多语言 | 12 组模型内置 | 按需下载 rec + dict |
| 中文模型 | 内置 | 手动下载（15.9 MB + 72 KB） |
| 识别结果 | `麻雀 Sparrow OCR memory test 12345` | 同左，完全一致 |

## 体积构成（RapidOcrNet，win-x64）

```text
15.9 MB  ch_PP-OCRv5_rec_mobile.onnx
15.4 MB  onnxruntime.dll
10.9 MB  libSkiaSharp.dll
 7.5 MB  latin_PP-OCRv5_rec_mobile_infer.onnx   ← 可删
 4.6 MB  ch_PP-OCRv5_mobile_det.onnx
 1.0 MB  ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx
 ~1.5 MB  其余托管 dll
```

## 结论

**RapidOcrNet 在体积（1/9）、内存（-40%）、速度（2×）上全面占优，识别结果一致。**

代价：

1. 模型不再随包内置，需要下载（中文 15.9 MB + 72 KB 词表）。
2. 多语言从「12 组内置」变成「按需下载 rec + dict」，需要模型管理。
3. 模型下载源是 ModelScope，需要写进文档/脚本。

## 决策（2026-11-12）

**保持 Sdcb.PaddleOCR，暂不替换。**

理由：

- 现有链路已通过 M1 验收，没有质量问题。
- 500 MB 对桌面工具可接受，不是用户可感知的瓶颈。
- 替换需要引入「模型下载 + 校验 + 多语言按需」一整套管理逻辑，收益与成本不成比例。
- RapidOcrNet 作为备选保留：`tools/RapidOcrProbe` 可随时复现对比。

重新评估的触发条件：

- 安装包体积成为分发障碍（比如需要走在线安装器）。
- 低配机器上 369 MB 峰值内存导致问题。
- 需要在不带 Paddle 原生库的平台上运行。

## 建议（如果将来替换）

分两步：

1. **先换引擎，只支持中文**：模型文件放在 `worker/models/v5/`，构建时若缺失则提示下载脚本。
2. **再做多语言**：按 `language` 下载对应的 `*_rec_mobile.onnx` + `*_dict.txt`。

模型文件 16 MB 不适合进 Git，`.gitignore` 已忽略 `*.onnx`；需要提供一个下载脚本或首次运行下载。

## 表格识别（2026-11-21）

PaddleOCR 的本地链路只做「检测 + 识别」，不理解表格结构，所以表格走了另一条路：

| 路线 | 做法 | 状态 |
|---|---|---|
| A. AI 视觉 | OCR 的「识别类型」选「表格 · HTML / Markdown」，把裁图发给支持图片的模型，提示词要求只输出表格 | ✅ 已实现 |
| B. 本地表格模型 | worker 内置 SLANet（`models/table`，7.7MB）+ 文字 OCR 回填，离线可用 | ✅ 已实现 |

A 的限制是**必须有视觉模型**（默认 qwen2.5vl 可以，离线翻译模型不行）；B 能把离线补齐，但 SLANet 对合并单元格和复杂版式不如大模型。

路由规则：`host.Ai.IsAvailable` 时先走 A，在线失败（非取消）记一条 WARN 后回落 B；没有配置视觉模型时直接走 B。

B 的实现要点（都是踩过的坑）：

- **模型要用 Paddle 3 导出的 PIR 版本**（`inference.json` + `inference.pdiparams`）。PaddleOCR 官网的 `ch_ppstructure_mobile_v2.0_SLANet_infer.tar` 是 Paddle 2.0 时代的产物，在 Paddle 3 的 oneDNN 下跑 `fused_conv2d` 会直接崩（`OneDnnContext does not have the input Filter`），而 `PaddleConfig` 不允许在创建后再关掉 oneDNN。PaddleX 的 `SLANet_plus_infer.tar`（`paddle3.0.0` 目录）可以直接跑，标签表仍然是同一份 48 项 `table_structure_dict_ch.txt`。
- **输入要先补成正方形**：`Sdcb.PaddleOCR` 的后处理用「归一化坐标 × 原图宽高」还原框，只有正方形输入才成立；非正方形图纵向会被压扁（900×420 的图，格子 y 只占原来的 0.45），文字就回填不进去。worker 里 `PadToSquare` 补黑边后再识别，坐标仍与原图一一对应。
- **SLANet 只给结构，文字要回填**：它输出的是 `<tr>/<td>` 片段（每个 `<td>` 一把框），格子里没有文字。worker 用文字 OCR 再跑一遍，按「文字框中心落在哪个格子」分配；中心出界时退回重叠面积。合成逻辑在插件工程的 `TableHtmlBuilder`（纯函数、有单测），worker 用 `Compile Include` 链接同一份源码，避免两边实现漂移。
- **`<td>` 有三种 token 形态**：`<td></td>`（整格一个 token）、`<td>`（后跟 `</td>`）、`<td` + 属性 + `>`（合并单元格）。文字要分别插到内容位置，否则 colspan 的格子会串位。
- 输出统一是完整 `<table>`（Sdcb 不给外层），Markdown 仍由本地 `HtmlTable.ToMarkdown` 转换。

实测（900×420 订单表，含表头 4 行 × 5 列）：本地识别 4.3s（含 SLANet + 文字模型首次加载），HTML 与 Markdown 的单元格内容、货币符号全部正确。

A 的输出是纯文本（HTML 或 Markdown），走 `OcrResultPayload`；截图识别时因为没有行框会回退到命令面板展示并复制。