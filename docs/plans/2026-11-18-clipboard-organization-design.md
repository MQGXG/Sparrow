# 2026-11-18 剪贴板管理增强：来源、时间分叠、撤销删除

> 状态：已实现
>
> 后续变更：存储已迁移到 SQLite，见
> [`2026-11-18-clipboard-sqlite-design.md`](2026-11-18-clipboard-sqlite-design.md)。

## 一、来源

- CopyAttach：按来源应用搜索 / 排除
- ClipLine：收藏区 + 今天 / 三日内 / 一周内 / 更早分叠
- TieZ：按内容 / 来源 / 日期搜索
- SyncClipboard：保留策略（本轮未做）

## 二、决策

1. **来源只记进程名**（`chrome` / `Code` / `explorer`），
   不记窗口标题、不记可执行文件路径。拿不到 owner 时留空。
2. **时间分叠只影响展示**，筛选/搜索仍作用在同一份条目上。
   收藏固定在最前，且不再重复出现在时间组里。
3. **撤销删除是内存快照**：删除时把条目 + 图片字节压栈（最多 20 条），
   `Restore` 以新 ID / 新时间写回，保留收藏、标签、来源。
   清空历史会同时清空撤销栈。
4. 不引入 SQLite：仍然是 JSONL + 内存筛选。

## 三、实现

- `ClipboardMonitor.ReadSourceApp()`：`GetClipboardOwner` → PID → `ProcessName`
- `ClipboardHistoryStore`：`SourceApp` 字段、`AllSourceApps()`、`Restore(entry, png)`
- `ClipboardTimeGroup`：纯函数决定分组，便于测试
- `ClipboardHistoryViewModel`：来源筛选、`Groups`、删除快照与撤销
- UI：来源下拉、分组标题、行内来源、撤销删除按钮

## 四、不做

来源排除规则（按应用不记录）、敏感内容遮罩、保留天数、SQLite、同步。
