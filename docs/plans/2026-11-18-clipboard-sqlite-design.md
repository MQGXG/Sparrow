# 2026-11-18 剪贴板历史迁移到 SQLite

> 状态：已实现

## 一、为什么现在换

JSONL 的代价在规模上来后变得具体：

- 每次复制 / 收藏 / 打标签都**重写整个文件**
- 启动全量加载，搜索线性扫描
- 没有事务，断电可能写坏最后一行
- 改一个收藏也要重写全部记录

用户明确要求使用数据库。此时历史已有收藏、标签、来源、保留策略，数据模型足够稳定，适合落库。

## 二、选型

- **Microsoft.Data.Sqlite 10.0.15**（自带 e_sqlite3，无需另装）
- WAL 模式，每次操作独立连接（连接池），所有写入在 `_gate` 内
- **FTS5 trigram** 全文索引：中文可按子串搜索；查询不足 3 字符时自动退化 LIKE
- 图片仍然放 `clipboard-images/`，数据库只存文件名（预览、拖出直接读文件，备份直观）

## 三、Schema

```sql
entries(
  id TEXT PRIMARY KEY,
  kind INTEGER, preview TEXT, text TEXT, image_file TEXT,
  paths TEXT,            -- JSON 数组
  content_hash TEXT, byte_size INTEGER, created_at INTEGER,
  favorite INTEGER, tags TEXT, auto_tags TEXT, source_app TEXT
)

entries_fts USING fts5(text, preview, tags, source_app,
                       content='entries', content_rowid='rowid',
                       tokenize='trigram')
```

`entries_fts` 由触发器与 `entries` 保持同步（insert / update / delete）。

## 四、行为保持

公开 API 不变，以下语义逐条保留：

- 同内容合并：按 `(kind, content_hash)` 找旧条目，继承收藏与标签
- 严格递增时间戳：毫秒相同则 +1（内存水位，启动时从 `MAX(created_at)` 恢复）
- 三重裁剪：天数 → 条数 → 容量，收藏全部豁免
- 搜索：先 FTS，短词 LIKE；标签、来源、摘要都参与
- 撤销删除：条目 + 图片字节仍在内存快照，`Restore` 用新 ID 写回
- 图片超 32MB 跳过、文本超 256K 只留摘要

## 五、迁移

- 只在**新建数据库**时检查同目录的 `clipboard-history.jsonl`
- 逐行解析，坏行跳过；收藏、标签、自动标签、来源、图片引用全部保留
- 导入在一个事务里；成功后把 JSONL 改名为 `.bak`
- 迁移失败保留 JSONL 与空数据库，下次启动可删库重试

## 六、测试

- 迁移：字段保留、坏行跳过、只执行一次
- 全文搜索：中文三字（FTS）、中文两字（LIKE 兜底）、大小写
- 原有存储测试全部保留（去重、裁剪、收藏、标签、撤销、保留策略）
- 当前总计 374 个测试通过

## 七、不做

- 图片 BLOB 入库（仍放文件）
- 加密存储（SQLite 默认明文，与之前一致）
- 跨设备同步
