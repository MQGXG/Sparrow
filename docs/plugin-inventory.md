# Sparrow 插件与内置功能清单

> 核对时间：2026-11-19
> 判定依据：`docs/plugin-model.md` 第十二节

## 判定口径

- **工具（插件）**：一次调用把输入变成输出，不碰 UI、不碰数据库、不碰剪贴板写入。
- **内置（宿主）**：产生输入、提供运行时、承载数据界面、统一结果出口。

工具台上能看见的工具卡 **全部来自 `plugins/`**；宿主没有任何内置工具注册表（`ToolCatalog` 只扫目录）。

## 一、插件工具（9 个）

| 插件目录 | 工具 ID | 显示名 | 输入 → 输出 | 依赖宿主能力 | 代码行数 |
|---|---|---|---|---:|---:|
| `sparrow.plugin.dev-base64` | `sparrow.dev.base64` | Base64 编解码 | Text → Text | 无 | 111 |
| `sparrow.plugin.json-format` | `sparrow.json.format` | JSON 格式化 | Text → Text | 无 | 126 |
| `sparrow.plugin.images-convert` | `sparrow.images.convert` | 图片格式转换 | Image → Image | 无 | 108 |
| `sparrow.plugin.images-resize` | `sparrow.images.resize` | 图片缩放 | Image → Image | 无 | 138 |
| `sparrow.plugin.files-hash` | `sparrow.files.hash` | 文件哈希 | Files → Table | 无 | 201 |
| `sparrow.plugin.ocr` | `sparrow.ocr.image` | 图片文字识别 | Images → Text | `Ocr`（独立 worker）+ `Ai`/`LocalTranslation` | 1223 |
| `sparrow.plugin.translate` | `sparrow.text.translate` | 翻译 | Text → Text | `Ai` + `LocalTranslation` | 443 |
| `sparrow.plugin.svg-prompt` | `sparrow.svg.prompt` | 提示词生成 SVG | Text → Svg | `Ai` + `SvgRender` | 132 |
| `sparrow.plugin.window-pin` | `sparrow.window.pin` | 窗口置顶 | None → Text | 无（用通用 `IWindowOverlay`） | 158 |

合计约 2330 行（不含已删除的剪贴板历史插件）。

## 二、内置功能（不是工具）

| 类别 | 内容 | 主要位置 | 为什么不是插件 |
|---|---|---|---|
| 输入采集 | 截图、冻结帧、区域/窗口/控件识别 | `CaptureWindow` 1440 行、`UiElementProbe` 653、`WindowCandidates`、`ScreenCapture` 271 | 产生 `ImageInput`，是工具的输入源 |
| 输入采集 | 滚动截图与拼接 | `ScrollCaptureService`、`ScrollStitcher` | 同上 |
| 输入采集 | 剪贴板监听、文本/图片/文件读取 | `ClipboardMonitor` 467、`Win32Clipboard` 377 | 系统信号 → `IToolInput` |
| 输入采集 | 文件拖入与按次授权 | `ScopedFileAccess`、`ToolContext.Files` | 安全边界必须在宿主 |
| 数据与界面 | 剪贴板历史（SQLite、收藏、标签、分类、时间分叠、来源筛选、缩略图） | `ClipboardHistoryStore` 1238、`ClipboardHistoryViewModel` 660 | 需要宿主数据读写 + 富 UI |
| 数据与界面 | 剪贴板详情、快速面板 | `ClipboardDetailView`、`ClipboardPanelWindow` | 同上 |
| 运行时 | 顺序粘贴队列、模拟按键 | `ClipboardPasteService`、`PasteInjector` | 插件没有输入注入能力，也不该有 |
| 运行时 | 拖拽出多格式（文本/位图/文件） | `ClipboardDragService` | 宿主结果出口 |
| 结果动作 | 复制、保存（图片落盘/文本另存）、固定、发送给其他工具 | `WorkspaceViewModel` 1372 | 所有工具共用出口 |
| 运行时 | 全局热键、托盘、命令面板、工作区窗口、设置页 | `App.axaml.cs` 886、`HotkeyService`、`TrayService` | 工具跑起来所需的外壳 |
| 运行时 | 插件扫描、加载、禁用、卸载 | `PluginScanner`、`PluginLoader`、`ToolCatalog` | 宿主自身 |
| 通用能力 | AI 端点与密钥、受控 HTTP | `AiClient` 317、`AiSecretStore` | 密钥与联网策略统一管 |
| 通用能力 | 窗口拾取遮罩与图钉 | `WindowOverlay`、`WindowPickerWindow` | 供插件调用的通用覆盖能力 |

## 三、边界上的特殊情况

| 项 | 现状 | 说明 |
|---|---|---|
| `sparrow.plugin.clipboard-history` | **已删除**（2026-11-19） | 曾为插件，后升级为工作区页，目录残留已清 |
| `window-pin` | 插件，但依赖宿主覆盖能力 | 遮挡/图钉在宿主，插件只做置顶逻辑，是最薄的一个 |
| OCR worker | 插件私有子进程 | 宿主不知道 worker 存在，符合边界 |
| 插件源码 | 全部只引用 `Sparrow.Plugin.Sdk` | 无一个引用 `Sparrow.App` |

## 四、规模对比

| | 行数 | 占比 |
|---|---:|---:|
| 宿主（`src/Sparrow.App`，不含 SDK） | 约 19275 | 约 91% |
| SDK 契约（`src/Sparrow.Plugin.Sdk`） | 约 820 | — |
| 插件（9 个） | 约 1835 | 约 9% |

Sparrow 的定位是「截图 / 剪贴板 / HUD 运行时 + 插件工具集」，不是通用插件平台；这个比例是健康的。
