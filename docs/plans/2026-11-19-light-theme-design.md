# 2026-11-19 浅色皮肤 / 多主题设计

> 状态：**待评估，未实现**
> 结论先说：可以做，且成本比预想低；建议只做「一套浅色」，不做多主题包。

## 一、现状盘点（2026-11-19 实测）

| 指标 | 数值 | 说明 |
|---|---|---|
| axaml 中硬编码颜色 | 53 处 | 其中 **40 处在 `src/Sparrow.App/Styles/Theme.axaml`**，即 token 定义本身 |
| 散落在各窗口的硬编码 | 13 处 | 全部是遮罩 / 阴影 / 光标色，见第四节 |
| `{StaticResource}` 引用 | 314 处 | 是「即时切换」方案的主要工作量 |
| C# 中硬编码色 | 5 处 | `HighlightTextBlock` 兜底色 + `CaptureWindow` 4 个选区画刷 |
| 写死深色 | 3 处 | `App.axaml`、`WorkspaceWindow.axaml`、`ClipboardPanelWindow.axaml` 的 `RequestedThemeVariant="Dark"` |

现有 token 分组（`Theme.axaml`）：

- 背景层级：`BgBrush` / `SurfaceBrush` / `SurfaceRaisedBrush` / `SurfaceHoverBrush`
- 描边：`LineBrush` / `LineStrongBrush` / `OverlayBrush`
- 文字三级：`TextPrimaryBrush` / `TextSecondaryBrush` / `TextMutedBrush`
- 强调：`AccentBrush` / `AccentHoverBrush` / `AccentPressedBrush` / `AccentSoftBrush` / `AccentBorderBrush` / `AccentSelectionBrush`
- 语义：`DangerBrush` / `DangerBgBrush` / `DangerLineBrush`
- 渐变：`WindowBgGradientBrush` / `SurfaceGradientBrush` / `PanelGradientBrush` / `AccentGradientBrush` / `AccentGradientHoverBrush`
- 纹理：`DotGridBrush`（`Assets/dot-grid.png`，32px 平铺）
- Fluent 覆盖：`SystemAccentColor*` 七个
- 字体：`UiFont` / `DisplayFont` / `MonoFont`

**结论：颜色已经高度 token 化，换主题 ≈ 换这 40 个值，不是满地图改颜色。**

## 二、必须先知道的三个坑

### 1. `{StaticResource}` 不随主题切换

Avalonia 的 `ThemeDictionaries`（Light / Dark 两套）只对 `{DynamicResource}` 生效。`StaticResource` 在 XAML 加载时取值，切主题后不会更新。

两条路：

- **重启生效**：只改 `Theme.axaml`，314 处引用不动；切换后提示「重启后生效」。第一版最快。
- **即时切换**：314 处批量替换为 `DynamicResource`，可像 VS Code 一样点一下立刻变；`DynamicResource` 有轻微查找开销，可忽略。

### 2. 琥珀在浅色底上不能当文字色

`#E5A44C` 放在 `#FAFAFA` 上对比度约 **2:1**，只够做填充，不够做文字 / 描边 / 图标。浅色主题必须把强调色拆成两组 token：

| token | 深色主题 | 浅色主题 | 用途 |
|---|---|---|---|
| `AccentFillBrush` | `#E5A44C` | `#E5A44C` | 主按钮底、进度、选中刻度线 |
| `AccentInkBrush` | `#E5A44C` | `#A96A16` | 强调文字、线性图标、光标、focus 环 |
| `AccentSoftBrush` | `#2C2318` | `#FBF1E2` | 选中行 / chip / 图标座底 |
| `AccentBorderBrush` | `#5C4A26` | `#E3C79A` | 描边、输入框聚焦边 |

现有 `AccentBrush` 在浅色下应等价于 `AccentInkBrush`；深色主题两者取值相同，所以改动对所有引用点透明。

### 3. 有些窗口必须恒定深色

截图 / 贴图类界面是「暗房」，浅色会让用户看不清图。**14 个窗口里只有 5 个需要做浅色：**

| 需要浅色 | 固定深色（不跟主题） |
|---|---|
| `WorkspaceWindow`（工具台，1187 行） | `CaptureWindow`（截图遮罩 / 冻结帧） |
| `SettingsView` | `ScrollCaptureWindow` / `ScrollResultWindow` |
| `ClipboardPanelWindow`（快速面板） | `PinnedImageWindow`（贴图） |
| `ClipboardDetailView` | `ClipboardImageZoomWindow`（灯箱） |
| `PaletteWindow`（命令面板） | `ToastWindow` / `WindowPickerWindow` / `PinIndicatorWindow` |

这条把工作量砍掉一半以上。

## 三、三套方案

| 方案 | 内容 | 估时 | 评价 |
|---|---|---|---|
| **A. 单套浅色 + 重启生效** | 改 `Theme.axaml` 为 `ThemeDictionaries`；5 个窗口目视校正；标题栏 / 阴影 / 点阵修正；设置页加「外观：深色 / 浅色」 | 1 天 | 性价比最高，先验证方向 |
| **B. 单套浅色 + 即时切换** | A + 314 处 `StaticResource` → `DynamicResource` | 1.5～2 天 | 体验完整，推荐最终形态 |
| **C. 多主题包** | B + token 外置为 JSON + 主题目录加载 + 每套 light/dark | 再 +2～3 天 | 不建议 |

### 为什么不建议 C

- 多主题包的价值是「用户自定义品牌色」，对本地工具类应用属于伪需求。
- 每多一套主题，所有新 UI 都要 ×N 验证，维护成本永久翻倍。
- Sparrow 的琥珀是识别度资产；浅色只要各一套就够覆盖「白天办公不刺眼」的真实场景。
- 参考对象 QcClip 做 9 套主题，是因为它以 GPUI 通用剪贴板定位；Sparrow 是截图 / OCR 工具，形态不同。

## 四、改动清单

### 4.1 `Styles/Theme.axaml`（核心）

- 40 个 brush 拆成 `ThemeDictionaries` 的 `Light` / `Dark` 两套，key 名保持不变（`BgBrush` 等）。
- 新增 `AccentFillBrush` / `AccentInkBrush` 两个语义 token。现有 `AccentBrush` 共 **33 处引用**，需逐条判断是「填充」还是「文字 / 描边 / 图标」，前者留 `AccentFillBrush`、后者改 `AccentInkBrush`。已知必须归到 ink 的：`Path.icon` 的 `Stroke`、`hud-tab Border.tick`、`Border.hud-tick`、`hud-chip.selected TextBlock.num`、输入框 `CaretBrush`、`ListBoxItem:selected Path.icon`。
- `AccentSelectionBrush` 现在是硬编码 `#20E5A44C`（12% 琥珀），浅色下需要提到约 18% 才看得见。
- `AccentSoftBrush` 现有 10 处、`AccentBorderBrush` 现有 12 处引用，只需换值，不改引用。
- `DotGridBrush` 需要浅色版本。

### 4.2 散落硬编码（13 处）

| 位置 | 现值 | 浅色处理 |
|---|---|---|
| `PinIndicatorWindow.axaml:18` | `#CC0B0E13` | 保持（恒定深色） |
| `CaptureWindow.axaml:20` | `#80000000` | 保持（遮罩恒定） |
| `CaptureWindow.axaml:28` | `#E5A44C` | 保持（选区框恒定琥珀） |
| `ClipboardImageZoomWindow.axaml:9` | `#F20B0E13` | 保持（灯箱恒定深色） |
| `ClipboardPanelWindow.axaml:313` | `#CC07090B` | 改为 `OverlayBrush` |
| `WorkspaceWindow.axaml:817` | `#CC07090B` | 改为 `OverlayBrush` |
| `WorkspaceWindow.axaml:1187` | `#D9000000` | 改为 `OverlayBrush` |
| `ToastWindow` / `PaletteWindow` / `PinnedImageWindow` / `ScrollCaptureWindow` 的 `BoxShadow #99000000` | 深色阴影 | 恒定深色窗口保持；跟随主题的降到 `#1A000000` |
| `HighlightTextBlock.cs:26` | `#E5A44C` | 改为从资源取，取不到时用 `AccentInk` |
| `CaptureWindow.axaml.cs:59-63` | 4 个选区画刷 | 保持（遮罩恒定） |

### 4.3 系统级适配

- `WindowChrome.TryApplyDarkTitleBar` 目前写死传 `1`。改为按主题传 `0` / `1`，否则浅色主题会出现「白色窗口 + 黑色系统标题栏」。
- `WindowChrome` 只处理标题栏；托盘菜单（`TrayService`）跟随系统，不需要改。
- `App.axaml` 与两个窗口的 `RequestedThemeVariant="Dark"` 改为跟随 `Application.RequestedThemeVariant`。
- `dot-grid.png`（100 字节，32×32）：浅色需要反相版本，或改为代码生成的 `DrawingBrush`（推荐，省一个资源文件）。

### 4.4 设置与持久化

- `AppSettings` 新增 `Appearance`（`dark` / `light`，默认 `dark`）。
- `SettingsViewModel` 增加外观下拉；切换后：
  - 方案 A：写设置 + `toast` 提示「重启后生效」
  - 方案 B：直接改 `Application.Current.RequestedThemeVariant` + 重设资源
- 首启动读取设置在 `App.axaml.cs` 初始化窗口之前完成。

### 4.5 浅色主题配色草案

| token | 深色 | 浅色 |
|---|---|---|
| `BgBrush` | `#0B0E13` | `#F7F8FA` |
| `SurfaceBrush` | `#12171F` | `#FFFFFF` |
| `SurfaceRaisedBrush` | `#1A212C` | `#FFFFFF` |
| `SurfaceHoverBrush` | `#242D3A` | `#EDEFF3` |
| `LineBrush` | `#2B3543` | `#E2E5EA` |
| `LineStrongBrush` | `#3D4959` | `#C9CFD8` |
| `TextPrimaryBrush` | `#E9EEF6` | `#141A22` |
| `TextSecondaryBrush` | `#9BA8BA` | `#4E5A6A` |
| `TextMutedBrush` | `#8695A8` | `#7A8796` |
| `AccentSoftBrush` | `#2C2318` | `#FBF1E2` |
| `AccentBorderBrush` | `#5C4A26` | `#E3C79A` |
| `DangerBrush` | `#F87171` | `#C0283A` |
| `DangerBgBrush` | `#2A1517` | `#FDECEF` |

主按钮（`Button.accent`）前景 `#1A1206` 在两种主题下都可保留：琥珀底上压深字。

## 五、验收标准

1. 工具台、设置、快速面板、详情、命令面板在浅色下无「深色块残留」、无低对比灰字。
2. 截图、贴图、灯箱、滚动截图在浅色主题下**外观完全不变**。
3. 切换主题后标题栏颜色一致（无黑白混搭）。
4. 焦点环、选中行、chip、`ToggleSwitch` 在浅色下仍以琥珀为唯一强调色。
5. 两种主题下 `hud-tab` 刻度线、`hud-tick`、工具卡片取景框角标都能看清。
6. 现有 401 个测试全绿；新增：设置持久化、主题应用不抛异常（headless 可测 token 解析）。

## 六、长期成本（必须接受）

> 做浅色之后，**每个新 UI / 每次样式改动都要在两种主题下各看一遍**。这是固定税，不是一次性投入。

如果这条不接受，就不要开始。

## 七、如果要做，建议顺序

1. `Theme.axaml` 拆 `ThemeDictionaries` + 加 `AccentInk` / `AccentFill`（此步完成后深色外观必须 1:1 不变）。
2. `WindowChrome` 按主题传参；`App.axaml` / 两个窗口去掉写死 `Dark`。
3. 5 个窗口逐个目视校正（工具台最费时）。
4. 设置页下拉 + 持久化 + toast。
5. 方案 B 追加：`StaticResource → DynamicResource` 批量替换 + 回归两套主题。
