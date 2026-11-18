# 2026-11-18 剪贴板历史升级为工作区页

> 状态：已实现
>
> 后续变更：存储已从 JSONL 迁移到 SQLite（WAL + FTS5），见
> [`2026-11-18-clipboard-sqlite-design.md`](2026-11-18-clipboard-sqlite-design.md)。

## 一、问题

自动记录剪贴板在 M1 只是一个插件工具：纯文本列表 + 「按序号还原」。
条目没有收藏、标签、分类，图片没有缩略图，删除/清空都做不了。
用户的原话是「感觉不是很好用」。

## 二、参考

| 产品 | 关键做法 |
|---|---|
| OmniSeeker（本地） | SQLite 历史：类型页签、收藏、Tags/Notes、详情编辑、排序 |
| CopyQ | 页签、标签/备注、按窗口/文本排除、敏感内容忽略 |
| Ditto | 分组、正则 + 进程排除、SQLite |
| EcoPaste | 收藏/固定/备注/分组、按来源应用与类型筛选、敏感内容跳过、保留策略 |
| Win+V | 25 条 + 固定 |

结论：这不是「换数据库」的问题，而是**数据模型 + 管理界面**的问题。

## 三、决策

1. **剪贴板历史升级为工作区独立页**（`工具 / 最近使用 / 剪贴板`），
   删除 `sparrow.plugin.clipboard-history` 插件与工具卡。
   理由：收藏/标签是写操作，富列表/缩略图需要 UI；插件按契约只能读，且不能拥有 UI。
2. **继续用 JSONL + PNG 目录**，不引入 SQLite。
   200～2000 条规模用内存过滤足够；等需要全文索引/几万条再换。
3. **自动标签保守**：链接 / 邮箱 / 颜色 / JSON / 路径；图片与文件按类型。
   不做代码猜测，避免误标。
4. **收藏不计入容量上限**，复制重复内容时保留收藏与标签。
5. **尊重 `Clipboard Viewer Ignore`**：密码管理器标记过的复制不记录。

## 四、实现

- 存储：`StoredEntry` 增加 `Favorite` / `Tags` / `AutoTags`；`Changed` 事件；
  `SetFavorite` / `SetTags` / `Delete` / `AllTags`；裁剪只裁非收藏。
- 分类：`ClipboardClassifier`（正则 + `JsonDocument` 校验）。
- 页面：`ClipboardHistoryViewModel` + `ClipboardEntryViewModel`；
  筛选、搜索、收藏、标签、详情、复制、删除、清空二次确认。
- 构建：删除插件目录后自动清理 `bin/.../plugins` 里的旧副本。
- SDK 1.6：`ClipboardHistoryEntry` 增加只读字段（向后兼容）。

## 五、不做

来源应用筛选、敏感内容正则、保留天数、加密存储、SQLite、粘贴回上一个应用。
这些留到有真实需求时再做。
