# Sparrow · 麻雀

> 一个热键，唤起你需要的那件小事。

**本地优先的 Windows 桌面工具集合。** 截图标注、屏幕录制、OCR 翻译、剪贴板历史、本机文件搜索、局域网互传，以及一个可以自由增删的插件工具台。

[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/Avalonia-12-8B44AC)](https://avaloniaui.net/)
[![Tests](https://img.shields.io/badge/tests-910%20passing-3DD68C)](#从源码构建)
[![License](https://img.shields.io/badge/License-MIT-3DD68C)](#协议)

---

## 功能

### 截图与标注

`Ctrl+Alt+C` 唤起全屏取景 → 框选 → 标注 → 复制 / 保存 / 固定 / 识别 / 滚动长截图。

- **冻结帧框选**：先冻结整屏再选，拖动时画面稳定不闪
- **智能候选**：悬停自动框出窗口 / 控件（按钮、输入框、列表项），单击选中，`Tab` 切层级
- **完整标注**：形状、箭头、画笔、高亮、文字、序号、马赛克、聚光灯、放大镜、橡皮
- **取色器**：跟随光标显示像素放大图 + HEX / RGB / HSL，`I` 复制
- **滚动截图**：自动滚动逐屏拼接，或手动滚轮；右侧缩略图实时预览，可裁掉头尾
- **OCR / 翻译**：框选即识别，支持 12 组语言；译文直接覆盖原图（对照 / 原文 / 译文三态）
- **标注模式**：固定到屏幕的图、滚动长图都能再次进入标注，工具条可自由拖动
- **多屏**：覆盖整个虚拟桌面，跨屏框选

### 屏幕录制

`Ctrl+Alt+Shift+R` 打开录制面板：区域 / 窗口 / 全屏。

- **区域录制**：截图工具条下方多一条录制条（停止 / 暂停 / 计时 / 麦克风 / 系统声 / 复制），录制中可直接在屏幕上标注，标注会录进视频
- **点击穿透**：选区外和未选工具时的选区内，鼠标直接落到下层应用，录制时电脑可以正常操作
- **窗口录制**：WGC 直接抓窗口表面，被其他窗口遮挡也能完整录到
- **硬件编码**：自动探测 NVENC / AMF / QSV / MediaFoundation，逐个试编后回退 `libx264`
- **格式**：MP4 / MKV / MOV / GIF / APNG
- **画质**：6 档 CRF（16–30）、编码级别、GOP 可调
- **帧率**：30 / 60 / 90 / 120 / 144，按分辨率自动限制上限，避免掉帧压时长
- **音频**：WASAPI 系统声音 + 麦克风，可分别开关
- **其他**：延时开始、定时停止、光标高亮（半径 / 颜色 / 透明度 / 阴影）、图文水印、一键复制录屏文件

### OCR 与翻译

- 插件薄壳 + 独立 worker（PP-OCRv5），一张卡支持剪贴板单图、拖入多图、选图
- 翻译可走在线 API 或本地模型（TranslateGemma 4B，首次使用自动下载）
- 识别结果按行框显示，可逐行 / 逐字选择复制，支持表格转 HTML / Markdown

### 剪贴板

- SQLite（WAL + FTS5）存储，文本保留 HTML / RTF 原格式，复制时一并写回
- 收藏、自定义标签、自动分类（链接 / 图片 / 文件）、时间分叠、来源应用筛选
- 顺序粘贴队列：`Ctrl+Alt+V` 逐条写入并模拟 `Ctrl+V`
- 尊重 `Clipboard Viewer Ignore`，可按应用「不记录」
- 拖出到其他应用（文本 / 位图 / 文件，`Shift` / `Ctrl` 可强制单格式）
- 保留天数与容量上限可配，收藏不受任何清理
- 可选接管 `Win+V`：开启后 `Win+V` 打开 Sparrow 剪贴板；关闭立即恢复系统快捷键

### 文件搜索

`Ctrl+Alt+F`（或连按两次 `Ctrl`）唤起本机文件搜索。

- 基于 NTFS `$MFT` 的全盘文件名索引，不遍历目录
- 索引由独立的 Windows 服务维护，宿主只读快照，不需要常驻管理员权限
- 首次建立索引需要管理员权限；可在 **设置 → 热键** 里选择索引盘、重建索引、安装 / 卸载索引服务
- 索引文件位于 `%ProgramData%\Sparrow\indexes`，只含文件名、路径等元数据，不读取文件内容
- 双击 `Ctrl` 与自定义热键可分别开关；热键留空表示只使用双击 `Ctrl`

### 局域网互传

与同一局域网内的 LocalSend / Sparrow 设备互发文件，不经过云端。

- 默认开启接收，App 常驻托盘时监听 `53317` 端口（被占用时自动顺延）
- 自动发现同网段设备，也支持手动输入 `IP:端口`
- 可设置接收 PIN；默认每次上传都要在确认框里点「接收」，可改为免确认
- 接收目录默认跟随系统 Downloads，可在 **设置 → 互传** 里修改
- 传输走 LocalSend 协议（HTTP / HTTPS，自签名证书），证书与状态文件在 `%LOCALAPPDATA%\Sparrow\LanShare`

### 工具台与插件

- `Ctrl+Alt+S` 命令面板：搜工具名，回车执行，输入直接取自剪贴板
- 工作区窗口：工具墙 / 剪贴板 / 工具台；最近使用保留为工具墙顶部 chips
- **每个工具都是插件**：一个目录 = 一个程序集 + 一份 `plugin.json`，删掉目录宿主照常启动
- 已内置 13 个插件：Base64、JSON 格式化、文件哈希、翻译、SVG 提示词、OCR、图片转换、图片缩放、窗口置顶、视频裁剪、视频压缩、视频转 GIF、视频抽帧

### 系统集成

- 全局热键，任意工具都能单独挂一个（按下直接执行，不经过面板）
- 系统托盘常驻，菜单可完全自定义
- 托盘可一键禁用全部全局热键，或仅在检测到独占全屏 D3D 游戏时自动禁用
- 开机自启（只启动到托盘）
- Velopack 安装包 + 增量更新

---

## 下载安装

从 [Releases](https://github.com/MQXGXG/Sparrow/releases) 下载最新安装包。

首次使用屏幕录制时，如果本机没有 FFmpeg，会自动下载到 `%LOCALAPPDATA%\Sparrow\ffmpeg`（约 80 MB，仅一次）。

---

## 快捷键

| 操作 | 默认快捷键 |
|---|---|
| 命令面板 | `Ctrl+Alt+S` |
| 文件搜索 | `Ctrl+Alt+F`（或双击 `Ctrl`） |
| 截图 / 标注 | `Ctrl+Alt+C` |
| 屏幕录制 | `Ctrl+Alt+Shift+R` |
| 剪贴板面板 | `Ctrl+Alt+B`（可在设置里接管 `Win+V`） |
| 顺序粘贴队列 | `Ctrl+Alt+V` |

热键可在 **设置 → 热键** 里改，输入框点一下直接按组合键即可录制；文件搜索的触发方式也在 **设置 → 热键** 里改。

---

## 数据与隐私

**所有处理都在本机完成，数据默认不出机器。**

| 数据 | 默认位置 |
|---|---|
| 配置、日志 | `%LOCALAPPDATA%\Sparrow` |
| 剪贴板历史 | `%LOCALAPPDATA%\Sparrow\clipboard.db` |
| 离线模型 | `%LOCALAPPDATA%\Sparrow\models` |
| 录制文件 | `%LOCALAPPDATA%\Sparrow\recordings` |
| 文件索引 | `%ProgramData%\Sparrow\indexes`（只含文件名、路径等元数据） |
| 互传证书 / 状态 | `%LOCALAPPDATA%\Sparrow\LanShare` |

**设置 → 数据** 里可以把数据根目录、模型目录、录制目录分别换到其他盘。

联网只发生在你显式触发时：AI 翻译 / 在线 OCR / 更新检查 / 首次下载 FFmpeg。

**例外：局域网互传默认开启。** 它只在同一局域网内监听 `53317` 端口，用于接收其他 LocalSend / Sparrow 设备发来的文件，不连接互联网；收到的文件写入本机接收目录。不需要时在 **设置 → 互传** 关闭「开启互传」，也可以设置接收 PIN 或关闭「免确认接收」。

---

## 插件开发

```text
plugins/
└─ sparrow.plugin.json-format/
   ├─ plugin.json
   └─ Sparrow.Plugin.Json.Format.dll
```

宿主不做「内置工具特殊处理」，随包分发的插件和你自己写的走同一条加载路径：

- 删目录 → 工具消失，不留死代码
- 禁用 → 不加载、不占内存、不进搜索
- 独立依赖 → 一个工具换库版本不影响其他工具

`Sparrow.Plugin.Sdk` 提供 `ITool`、`IAiClient`、`IWindowOverlay` 等能力，输入 / 结果 / 运行时归宿主，工具一律插件。

详见 [`docs/plugin-model.md`](docs/plugin-model.md) 与 [`docs/tool-development.md`](docs/tool-development.md)。

---

## 从源码构建

```bash
dotnet build Sparrow.slnx
dotnet run --project src/Sparrow.App
```

构建 App 时会先构建 `plugins/`，插件产物自动复制到 `src/Sparrow.App/bin/<配置>/net10.0-windows10.0.19041.0/plugins/`。

一次构建全部（含 Release）：

```powershell
./build/run.ps1
```

运行测试：

```bash
dotnet test tests/Sparrow.Tests/Sparrow.Tests.csproj
```

技术栈：C# 14 / .NET 10、Avalonia 12、CommunityToolkit.Mvvm、SQLite + FTS5、SkiaSharp、Svg.Skia、Velopack、PaddleOCR、Lertaro（MFT 索引）。

---

## 常见问题

**识别 / 翻译 / 提示词插件没出现？**
先执行一次 `dotnet build Sparrow.slnx`，确认 `src/Sparrow.App/bin/<配置>/net10.0-windows10.0.19041.0/plugins/` 里有对应目录。

**录制提示找不到 FFmpeg？**
首次录制会自动下载；也可以运行 `scripts/fetch-ffmpeg.ps1` 手动拉到 `tools/ffmpeg/`。

**为什么录制时屏幕边缘有一圈黄色高亮？**
这是 Windows Graphics Capture 的捕获指示器，系统隐私提示。Windows 11 22H2+ 会自动关闭，Windows 10 上系统强制显示。

**中文输入法下按回车不执行？**
第一次回车让输入法上屏，第二次才是执行。

---

## 协议

本项目使用 **MIT License**，完整条款见 [`LICENSE`](LICENSE)。

唯一的条件是：**分发或修改时保留版权声明和许可声明**。商用、闭源修改、再分发都没有限制。

**第三方组件：**

- [Avalonia](https://avaloniaui.net/)、[SkiaSharp](https://github.com/mono/SkiaSharp)、[Svg.Skia](https://github.com/wieslawsoltes/Svg.Skia)、[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)、[Velopack](https://velopack.io/) 均为 MIT
- [Lertaro](https://github.com/Lertaro/Lertaro) 为 MIT；`src/third_party/Lertaro` 用于 NTFS `$MFT` 扫描、快照索引与文件名查询
- [PaddleOCR / PP-OCRv5](https://github.com/PaddlePaddle/PaddleOCR) 为 Apache-2.0，仅 OCR worker 使用
- [FFmpeg](https://ffmpeg.org/) 以**独立进程**方式调用，不链接、不修改。项目本身下载的构建为 GPL 版，其许可与源码获取方式见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)
- 如果二次分发包含 `ffmpeg.exe` 的安装包，需要同时遵守 FFmpeg 的 GPL 条款

---

## 文档

| 想了解 | 看这份 |
|---|---|
| 有哪些工具、做到什么程度 | [`docs/tool-catalog.md`](docs/tool-catalog.md) |
| 插件如何加载与隔离 | [`docs/plugin-model.md`](docs/plugin-model.md) |
| 整体架构 | [`docs/architecture.md`](docs/architecture.md) |
| 加一个新工具 | [`docs/tool-development.md`](docs/tool-development.md) |
| 界面规范 | [`docs/ui-spec.md`](docs/ui-spec.md) |
| 性能与内存 | [`docs/performance.md`](docs/performance.md) |
| 发布检查清单 | [`docs/release-checklist.md`](docs/release-checklist.md) |

---

## 三条原则

1. **一次热键闭环** —— 从唤起到拿到结果，不超过一次热键 + 一次确认。
2. **本地默认** —— 数据不出本机；互联网访问必须是显式触发的可选增强。局域网互传是唯一默认开启的局域网点对点能力，可在设置里关闭。
3. **每个工具都是插件** —— 加工具是加一个目录，删工具是删一个目录。

> 麻雀虽小，五脏俱全。
