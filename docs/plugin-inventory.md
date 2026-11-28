# Sparrow 插件与内置功能清单

> 核对时间：2026-11-28
> 补充判定：2026-11-25（屏幕录制归类）、2026-11-28（文件搜索 / 局域网互传归类）
> 判定依据：`docs/plugin-model.md` 第十二节

## 判定口径

- **工具（插件）**：一次调用把输入变成输出，不碰 UI、不碰数据库、不碰剪贴板写入。
- **内置（宿主）**：产生输入、提供运行时、承载数据界面、统一结果出口。

工具台上能看见的工具卡 **全部来自 `plugins/`**；宿主没有任何内置工具注册表（`ToolCatalog` 只扫目录）。

## 一、插件工具（13 个）

行数为 2026-11-28 实测，统计该目录下的 `.cs` / `.axaml`，不含 `bin` / `obj`；OCR 的 worker 单列。

| 插件目录 | 工具 ID | 显示名 | 输入 → 输出 | 依赖宿主能力 | 代码行数 |
|---|---|---|---|---|---:|
| `sparrow.plugin.dev-base64` | `sparrow.dev.base64` | Base64 编解码 | Text → Text | 无 | 96 |
| `sparrow.plugin.json-format` | `sparrow.json.format` | JSON 格式化 | Text → Text | 无 | 114 |
| `sparrow.plugin.images-convert` | `sparrow.images.convert` | 图片格式转换 | Image → Image | 无 | 96 |
| `sparrow.plugin.images-resize` | `sparrow.images.resize` | 图片缩放 | Image → Image | 无 | 120 |
| `sparrow.plugin.files-hash` | `sparrow.files.hash` | 文件哈希 | Files → Table | 无 | 172 |
| `sparrow.plugin.video-trim` | `sparrow.video.trim` | 视频裁剪 | Files → FileSet | 无（用宿主目录的 ffmpeg） | 134 |
| `sparrow.plugin.video-compress` | `sparrow.video.compress` | 视频压缩 | Files → FileSet | 无（用宿主目录的 ffmpeg） | 159 |
| `sparrow.plugin.video-gif` | `sparrow.video.toGif` | 视频转 GIF | Files → FileSet | 无（用宿主目录的 ffmpeg） | 131 |
| `sparrow.plugin.video-frames` | `sparrow.video.frames` | 视频抽帧 | Files → FileSet | 无（用宿主目录的 ffmpeg） | 125 |
| `sparrow.plugin.ocr` | `sparrow.ocr.image` | 图片文字识别 | Images → Text | `Ocr`（独立 worker，含 SLANet 表格模型）+ `Ai` / `LocalTranslation` | 1838（worker 另 240） |
| `sparrow.plugin.translate` | `sparrow.text.translate` | 翻译 | Text → Text | `Ai` + `LocalTranslation` | 381 |
| `sparrow.plugin.svg-prompt` | `sparrow.svg.prompt` | 提示词生成 SVG | Text → Svg | `Ai` + `SvgRender` | 118 |
| `sparrow.plugin.window-pin` | `sparrow.window.pin` | 窗口置顶 | None → Text | 无（用通用 `IWindowOverlay`） | 144 |

合计约 3,628 行（不含 OCR worker）；含 worker 为 3,868 行。

## 二、内置功能（不是工具）

| 类别 | 内容 | 主要位置 | 为什么不是插件 |
|---|---|---|---|
| 输入采集 | 截图、冻结帧、区域 / 窗口 / 控件识别 | `Views/CaptureWindow.*`（17 文件 / 11,426 行）、`UiElementProbe` 377、`ScreenCapture` 383 | 产生 `ImageInput`，是工具的输入源 |
| 输入采集 | 滚动截图与拼接 | `ScrollCaptureService`、`ScrollStitcher` | 同上 |
| 输入采集 | **屏幕录制会话（MVP）** | `Services/Recording`（15 文件 / 3,200 行，WGC 抓帧 + 控制条）、`Overlays/RecordingBarWindow` | 会话型采集：开始 / 停止、控制条、流式落盘、停止热键都在宿主 |
| 输入采集 | 剪贴板监听、文本 / 图片 / 文件读取 | `ClipboardMonitor` 411、`Win32Clipboard` 409 | 系统信号 → `IToolInput` |
| 输入采集 | 文件拖入与按次授权 | `ScopedFileAccess`、`ToolContext.Files` | 安全边界必须在宿主 |
| 输入采集 | **本机文件搜索** | `MftFileIndex` 341、`SimpleFileSearchService` 250、`DoubleCtrlHotkey` 196；`Sparrow.IndexService`（261 行） | 索引由 SYSTEM 服务维护，宿主只读快照；输入来自全盘文件名，不是一次工具调用 |
| 数据与界面 | 剪贴板历史（SQLite、收藏、标签、分类、时间分叠、来源筛选、缩略图） | `ClipboardHistoryStore.*`（7 文件 / 1,262 行）、`ClipboardHistoryViewModel` 755 | 需要宿主数据读写 + 富 UI |
| 数据与界面 | 剪贴板详情、快速面板 | `ClipboardDetailView`、`ClipboardPanelWindow` | 同上 |
| 运行时 | 顺序粘贴队列、模拟按键 | `ClipboardPasteService`、`PasteInjector` | 插件没有输入注入能力，也不该有 |
| 运行时 | 拖拽出多格式（文本 / 位图 / 文件） | `ClipboardDragService` | 宿主结果出口 |
| 结果动作 | 复制、保存（图片落盘 / 文本另存）、固定、发送给其他工具 | `WorkspaceViewModel.*`（6 文件 / 1,412 行） | 所有工具共用出口 |
| 运行时 | 全局热键、托盘、命令面板、工作区窗口、设置页 | `App.*`（7 文件 / 2,182 行）、`HotkeyService`、`TrayService` | 工具跑起来所需的外壳 |
| 运行时 | 插件扫描、加载、禁用、卸载 | `PluginScanner`、`PluginLoader`、`ToolCatalog` | 宿主自身 |
| 运行时 | **局域网互传** | `Services/LanShare`（46 文件 / 3,917 行）、`LanShareWindow` | 常驻网络监听、TLS 服务端、接收确认与落盘都在宿主；不是一次输入 → 输出 |
| 通用能力 | AI 端点与密钥、受控 HTTP | `AiClient`、`AiSecretStore` | 密钥与联网策略统一管 |
| 通用能力 | 窗口拾取遮罩与图钉 | `WindowOverlay`、`WindowPickerWindow` | 供插件调用的通用覆盖能力 |

## 三、边界上的特殊情况

| 项 | 现状 | 说明 |
|---|---|---|
| `sparrow.plugin.clipboard-history` | **已删除**（2026-11-19） | 曾为插件，后升级为工作区页，目录残留已清 |
| `window-pin` | 插件，但依赖宿主覆盖能力 | 遮挡 / 图钉在宿主，插件只做置顶逻辑，是最薄的一个 |
| 屏幕录制 | **宿主能力，不是插件**（P2 规划） | 会话型 + 流式写盘 + 控制条，五条硬约束见 `plugin-model.md`；录制后的转 GIF / 压缩 / 裁剪才是插件 |
| 文件搜索 | **宿主能力，不是插件** | 索引服务需要 SYSTEM 权限与 USN 实时监听；搜索面板是宿主输入源 |
| 局域网互传 | **宿主能力，不是插件** | 需要常驻监听端口、自签名 TLS、接收确认与文件落盘；不是无状态工具调用 |
| OCR worker | 插件私有子进程 | 宿主不知道 worker 存在，符合边界 |
| 插件源码 | 插件主项目只引用 `Sparrow.Plugin.Sdk` | 无一个引用 `Sparrow.App`；OCR worker 是插件私有子项目 |

## 四、规模对比

统计范围：`src/Sparrow.App`、`src/Sparrow.Plugin.Sdk`、`plugins/*`，不含 `bin` / `obj`。

| | 行数 | 占比 |
|---|---:|---:|
| 宿主（`src/Sparrow.App`，不含 SDK） | 55,577 | 约 92% |
| SDK 契约（`src/Sparrow.Plugin.Sdk`） | 1,021 | 约 2% |
| 插件（13 个，含 OCR worker） | 3,868 | 约 6% |

不计入上表：

- 索引 vendor（`src/third_party/Lertaro`）：28,895 行
- 索引服务（`src/Sparrow.IndexService`）：261 行
- 测试（`tests/Sparrow.Tests`）：15,973 行

宿主仍然是绝对大头；插件总量不到 4 千行，说明真正的复杂度集中在截图、录制、剪贴板、文件索引、局域网互传这些宿主级能力上。Sparrow 的定位是「截图 / 剪贴板 / HUD 运行时 + 插件工具集」，不是通用插件平台；这个比例是健康的。
