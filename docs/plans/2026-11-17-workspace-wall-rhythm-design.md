# 工具墙布局节奏 + 导航修复 · 设计

日期：2026-11-17
状态：已确认

## 背景与问题

两个问题：

1. **导航卡死（Bug）**：「工具 / 最近使用」tab 行位于工具墙的 `ScrollViewer` 内部
   （`IsVisible="{Binding IsWallVisible}"`）。从「最近使用」页进工具台 → 执行 → 点「‹ 返回」
   后停在最近页，而最近页没有任何回工具墙的入口（搜索框也被
   `IsVisible="{Binding !IsRecentOpen}"` 隐藏），导航彻底卡死。
2. **布局单调机械**：8 张 236×136 等大卡片组成无差别网格，无主次、无分组层次；
   同一份执行历史在「最近执行」条与「最近使用」页出现两次，信息冗余。

## 决策记录

| 决策点 | 结论 |
|---|---|
| 破局方向 | 布局节奏（保持现有 HUD 风格，不引入新视觉体系） |
| 工具墙底部「最近执行」条 | 删除（与「最近使用」页执行记录完全重复） |
| 大卡归属 | 最近执行过的工具，**单张**双倍宽大卡 |
| 大卡内容 | 图标 + 名称 + 描述 + 上次执行时间 + 结果摘要前 60 字（数据来自 `_allHistory[0]`） |
| 小卡分组 | 按分类分组，`hud-tick` 组标题，复用 VM 已有的 `Groups` |
| 分类 chip 行为 | 从「过滤」改为「折叠联动」：点 chip 展开该组、其余折叠；「全部」展开所有 |
| 组标题 | 可点击手动折叠/展开，状态记忆在 VM 的 `HashSet<string>` |
| Esc | 工具台补 Esc 返回（与命令面板一致） |

## 设计

### 1. 导航卡死修复

- tab 行（「工具 / 最近使用」+ TOOLS 读数）从墙的 `ScrollViewer` 提到共享层：
  只要 `!IsDetailOpen` 就常驻可见，墙页与最近页共用。
- 搜索框去掉 `IsVisible="{Binding !IsRecentOpen}"`，两页都保留；
  最近页输入搜索词即触发 `RefilterTools` 自动回墙（`SelectCategory`/`OnToolQueryChanged`
  已会清 `IsRecentOpen` 的路径保持），导航天然打通。
- 工具台（detail）增加 Esc 键返回：`WorkspaceWindow` 隧道阶段拦截 KeyDown，
  `IsDetailOpen == true` 时调用 `CloseDetail()` 并 `Handled = true`。

### 2. 删除「最近执行」条

`WorkspaceWindow.axaml` 中墙页底部的「最近执行」StackPanel 整块删除。
数据链路（`_allHistory`、`FilterHistory`、`RebuildRecentTools`、「最近使用」页）不动。

### 3. 墙顶大卡

- 位置：分类 chip 行下方；`HasHistory && RecentTools.Count > 0` 时显示，否则整块隐藏。
- 尺寸：双倍宽（≈488×136，两张小卡宽 + 间距），沿用 `tool-card` 样式与取景框角标。
- 内容：大图标（48px）+ 名称 + 描述（2 行）；右列竖排：
  `上次执行` 时间（`num` 等宽）、结果摘要前 60 字（`TextTrimming`）。
- 数据：`RecentTools[0]`（已有 `ToolItemViewModel` + `TimeLabel`）；
  摘要取 `_allHistory` 中该工具首条的 `Preview`/`Output` 截断。
  新增 VM 只读属性 `FeatureRecentTool`（或复用 `RecentTools[0]` + 新增摘要属性）。
- 点击 = `OpenTool`（走现有 `OnRecentToolClick` 路径，`fromRecent: false`）。
- 新用户无历史：不显示，不留空壳。

### 4. 分组 + chip 联动折叠

- 小卡区从扁平 `VisibleTools` 改为按 `Groups`（`ToolGroupViewModel`）渲染：
  每组 = `hud-tick` + 「分类名 · 数量」组标题（可点击）+ 组内等大小卡（WrapPanel）。
- VM 新增：
  - `CollapsedCategories : HashSet<string>`（折叠状态记忆）
  - `IsGroupExpanded(category)` / `ToggleGroup(category)`，变更通知组标题与卡片可见性
  - `SelectCategory` 语义改为：选中 chip → 展开该组、其余组折叠；
    「全部」→ 全部展开；不再从 `VisibleTools` 里过滤移除。
- 可见性规则（组级）：
  1. `ToolQuery` 非空 → 全部展开（搜索结果不被折叠遮蔽），忽略折叠状态；
  2. 选中具体 chip → 该组展开、其余折叠；
  3. 「全部」+ 查询为空 → 按 `CollapsedCategories` 手动折叠状态。
- `RefilterTools` 保留搜索过滤语义；分类过滤分支移除（分类不再移除卡片）。
- 空态（`IsToolListEmpty`）逻辑不变。

### 5. 错误处理与边界

- 无历史 → 大卡隐藏；仅 1 个分组 → 组标题照常显示（保持结构可预期）。
- 折叠后组内 0 张可见卡 → 组标题仍显示（用户可再展开），不产生"空白组"错觉：
  组标题带数量徽标，数量是总数而非可见数。
- Esc 在设置页不拦截（设置页有自己的「← 返回」）；在工具台外不拦截。

### 6. 测试

- Headless 回归：打开最近页 → 断言 tab 行控件 `IsVisible == true`（防这条 bug 回归）。
- VM 测试：
  - 点 chip 后仅该组展开、其余折叠；
  - 搜索词非空时强制全展开；
  - `ToggleGroup` 状态记忆与通知。
- 存量 231 个测试保持全绿（不碰数据链路）。
