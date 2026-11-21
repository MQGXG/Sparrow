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
| B. 本地表格模型 | `Sdcb.PaddleOCR 3.3.1` 自带 `PaddleOcrTableRecognizer` + `TableRecognitionModel.FromDirectory`，随 worker 内置 SLANet（约 10MB） | 未做 |

A 的限制是**必须有视觉模型**（默认 qwen2.5vl 可以，离线翻译模型不行）；B 能把离线补齐，但 SLANet 对合并单元格和复杂版式不如大模型。

A 的输出是纯文本（HTML 或 Markdown），走 `OcrResultPayload`；截图识别时因为没有行框会回退到命令面板展示并复制。