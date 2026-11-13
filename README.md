# Sparrow · 麻雀

> 麻雀虽小，五脏俱全。一个热键，唤起你需要的全部工具。

**本地优先的桌面上下文工具集合。**

---

## 它是什么

Sparrow 是一个软件，里面装着一组小工具。

不是「一个功能很大的软件」，而是「**一个入口 + 一组互不相干的小工具**」：

```text
剪贴板里有一段 JSON    → 热键 → 格式化 → 复制走
屏幕上有一张截图        → 热键 → 圈选 → 识别文字 → 复制走
剪贴板里有一段乱文本    → 热键 → 去重排序 → 复制走
```

三个工具互不知道对方存在，但它们共享三样东西：

| 共享的是什么 | 具体指 |
|---|---|
| **同一个入口** | 一个全局热键唤起的命令面板，搜工具名直接执行 |
| **同一种上下文** | 剪贴板 / 选区 / 截图 / 文件 / 手输，统一成同一个输入对象 |
| **同一套结果动作** | 复制 / 保存 / 固定 / 发送给其他工具 / 打开所在位置 |

这三样是「集合」，第四样不是——**工具本身必须能独立存在**。

## 每个工具都是一个插件

这是硬性架构约束，不是口号：

```text
一个工具 = 一个插件目录 = 一个独立程序集 + 一份清单
```

```text
plugins/
├─ sparrow.plugin.clipboard-history/
├─ sparrow.plugin.json-format/
└─ sparrow.plugin.ocr-region/
```

由此得到三个能力：

| 能力 | 表现 |
|---|---|
| **能删** | 删掉一个插件目录，宿主照常启动，面板里就没有这个工具了，不留死代码 |
| **能禁用** | 禁用后不加载、不占内存、不进搜索结果 |
| **能独立演进** | 一个工具升级依赖（比如 SVG 库换版本）不影响其他任何工具 |

**宿主里没有「内置工具」这个概念**——随包分发的工具和用户自己放的工具走完全同一条加载路径。否则半年内必然出现「这个功能特殊，我直接写进宿主吧」，边界一破，集合就退化成单体。

代价是每个插件有几个样板文件，用 `dotnet new sparrow-tool` 一条命令抹平。

细节见 [`docs/plugin-model.md`](docs/plugin-model.md)。

---

## 它不是什么

- 不是 AI 应用。不配任何模型也能完整使用。
- 不是 SaaS。没有账号、没有云同步、没有订阅。
- 不是平台。不做插件市场、不做脚本运行时、不做工作流编排。
- 不是「大而全的瑞士军刀」。**工具多不等于好用，一次热键解决一件事才算。**

---

## 现状

| 项 | 状态 |
|---|---|
| 设计文档 | ✅ 完成（本目录 `docs/`） |
| 契约 `Sparrow.Plugin.Sdk` | ✅ 完成，编译 0 警告 |
| 插件 `JsonFormat` | ✅ 完成 |
| 插件 `Base64` | ✅ 完成（第二个插件，验证「加插件不改宿主」） |
| 插件 `Translate` | ✅ 完成：AI 翻译；宿主统一 provider / 密钥，插件只拿 `IAiClient` |
| 插件 `SVG Prompt` | ✅ 完成：提示词 → SVG；宿主清洗后用 Svg.Skia 渲染预览 |
| 最小可运行壳 | ✅ 完成：托盘 + `Ctrl+Alt+S` + 命令面板 + 剪贴板闭环 |
| 插件扫描 + ALC 隔离 | ✅ 完成：`plugins/` 目录发现、懒加载、独立加载上下文 |
| 设置 / 禁用 / 卸载 | ✅ 完成：`settings.json`、`disabledPlugins`、ALC 回收断言 |
| 宿主能力注入 | ✅ 完成：`ToolContext.Host`、`PluginDirectory`、网络默认关闭、`IAiClient` |
| 剪贴板图片 | ✅ 完成：`CF_DIB` → PNG（SkiaSharp） |
| 插件 `OCR` | ✅ 完成：进程插件 + PP-OCRv5 真实识别（12 组语言，~2.1–2.7s/张） |
| 工作区窗口 | ✅ 完成：工具墙 / 最近使用 / 工具台三态 + 分类导航 + 自动参数 + 设置页（插件开关、模型） |
| 界面 | ✅ 完成：统一设计 token（`Styles/Theme.axaml`），深色冷灰 + 单一琥珀强调色 |
| 截图识别 | ✅ 完成：`Ctrl+Alt+C` 选区 → 动作栏（识别文字 / 复制图片 / 保存 / 固定 / 滚动 / 取消），可全程键盘，覆盖整个虚拟桌面 |
| 滚动截图 | ✅ 完成：选区滚动拼接，1200×1000 选区实测拼出 1200×3100，无重复无断层 |
| 执行历史 | ✅ 完成：JSONL 落盘，工作区右栏显示最近 20 条 + 清空 |
| 热键设置 | ✅ 完成：`settings.json` 可改，格式 `Ctrl+Alt+S`，重启生效 |
| 开机自启 | ✅ 完成：HKCU Run 项，只启动到托盘 |
| M1 验收 | ✅ 完成：9 条出口条件全部达标（见 `docs/acceptance.md`） |
| 测试 | ✅ 187 个通过（含 headless 键盘路由、真实剪贴板取图/写图、真实子进程 OCR、滚动拼接、工具墙 ViewModel、AI 请求/密钥加密、SVG 清洗/渲染） |
| M1（剪贴板历史 / 截图 / PaddleOCR-VL） | ⬜ 未开始 |

当前阶段：**M1 插件基础设施完成**——加插件不改宿主、能禁用、能卸载且内存真的回收。
下一步是剪贴板历史插件与 OCR。见 [`docs/roadmap.md`](docs/roadmap.md)。

---

## 文档地图

给不同目的的人看不同的文档：

| 你想… | 看这份 |
|---|---|
| 知道这软件到底做哪些工具 | [`docs/tool-catalog.md`](docs/tool-catalog.md) |
| 知道这一版做什么、不做什么 | [`docs/scope.md`](docs/scope.md) |
| **搞清楚插件怎么加载、怎么隔离** | [`docs/plugin-model.md`](docs/plugin-model.md) |
| 动手写代码 | [`docs/architecture.md`](docs/architecture.md) |
| 加一个新工具 | [`docs/tool-development.md`](docs/tool-development.md) |
| 搞清楚「上下文」怎么统一 | [`docs/context-model.md`](docs/context-model.md) |
| 改界面 | [`docs/ui-spec.md`](docs/ui-spec.md) |
| 知道什么时候能发版 | [`docs/release-checklist.md`](docs/release-checklist.md) |

---

## 技术栈

```text
C# 14 + .NET 10 LTS (2028-11 支持到期)
Avalonia 12.1.6        跨平台桌面 UI
CommunityToolkit.Mvvm  MVVM 源生成器
SQLite + Serilog       本地存储与日志
SkiaSharp 3.119.4      图像解码 / 栅格化（版本跟随 Avalonia，勿升 4.x）
Svg.Skia 5.1.1         SVG 渲染（勿升 5.2.x，会撞 SkiaSharp 4.x）
Velopack               安装与增量更新
```

版本为什么钉死、哪些包不能升，见 [`docs/architecture.md`](docs/architecture.md#版本钉死)。

---

## 跑起来

```bash
dotnet run --project src/Sparrow.App
```

App 的 csproj 里有一组**构建期**插件引用（glob，`ReferenceOutputAssembly=false`），
构建 App 时会先构建 `plugins/`，插件再把产物复制到
`src/Sparrow.App/bin/<配置>/net10.0/plugins/`。所以正常 `dotnet run` 就能看到所有工具。

发版或需要 Release 一次建全时：

```powershell
./build/run.ps1
```

> 如果工作区里看不到「翻译」「提示词生成 SVG」，先确认
> `src/Sparrow.App/bin/<配置>/net10.0/plugins/sparrow.plugin.translate/` 是否存在。
> 不存在就执行一次 `dotnet build Sparrow.slnx`，不要继续用旧的 `bin`。
> 也不要使用 `artifacts/` 里的旧 zip。

插件产物会被自动同步到 `src/Sparrow.App/bin/<配置>/net10.0/plugins/`，
每个插件一个目录，目录名等于它的 `plugin.json` 里的 `id`。

启动后没有主窗口，托盘出现麻雀图标：

1. 按 `Ctrl+Alt+S`（或点托盘）唤起面板
2. 输入 `json` 过滤工具
3. 回车执行，输入取自剪贴板
4. `Ctrl+C` 或点「复制」把结果写回剪贴板
5. `Esc` 关闭面板

> 中文输入法下要按**两次**回车：第一次让输入法把 `json` 上屏，第二次才是执行。
> 原因见 [`docs/ui-spec.md` 6.1](docs/ui-spec.md#61-中文输入法下的-enter实测踩到的坑)。

M0 的面板是**只读结果**：不编辑、不保存、不发到其他工具，只负责「看一眼、复制走」。

---

## 三条不可违背的原则

1. **一次热键闭环** —— 任何工具，从唤起到拿到结果，不超过一次热键 + 一次确认。
2. **本地默认** —— 数据不出本机；联网必须是用户显式触发的可选增强。
3. **每个工具都是插件** —— 加工具是加一个插件目录，不改宿主；删工具是删一个目录，不留死代码。
