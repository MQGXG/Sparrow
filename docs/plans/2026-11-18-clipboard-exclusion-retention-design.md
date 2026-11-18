# 2026-11-18 剪贴板记录范围与保留策略

> 状态：已实现

## 一、问题

- 密码管理器、银行页面、聊天验证码被无差别记录；总开关太粗。
- 只有「200 条」一个限制，200 张图片可能占几个 GB，旧内容永不消失。

## 二、决策

### 按应用排除

- 名单存 `settings.json` 的 `ClipboardExcludedApps`，存**进程名**（`1Password` / `chrome`）。
- 只在 `ClipboardMonitor.ReadClipboard` 读取前判断；**只影响之后的复制**，
  已记录内容保留（用户可先按来源筛选再手动删除）。
- 入口：剪贴板页来源下拉旁的「不记录 X / 恢复记录 X」按钮；
  设置页也能直接写配置。
- 拿不到 owner（UWP / 管理员程序）时无法判断，按「不排除」处理。

### 保留策略

三重限制，全部由 `ClipboardHistoryStore.TrimAndPersist` 执行：

1. 条数：非收藏最多 `_maxEntries`
2. 天数：非收藏超过 `_retentionDays`（0 = 永久）
3. 总容量：非收藏占用超过 `_maxBytes`（0 = 不限制），从最旧的开始删

**收藏条目不参与任何一项清理**，也不被容量清理连带删除。
设置页改完通过 `ConfigureRetention` 立即清理一次并发 `Changed` 刷新页面。

容量用 `ByteSize` 近似：文本按字符数、图片按 PNG 字节数。

## 三、实现

- `AppSettings`：`ClipboardExcludedApps` / `ClipboardHistoryRetentionDays` / `ClipboardHistoryMaxBytes`
- `ClipboardMonitor.SetExcludedApps` + 读取前判断（引用交换，监听线程不持锁）
- `ClipboardHistoryStore`：构造参数、`ConfigureRetention`、`ApproximateBytes`、三步裁剪
- `IClipboardExclusionStore`：页面 VM 只依赖接口，设置页实现落盘
- 设置页：保留天数 / 总容量两个 `NumericUpDown`
- 剪贴板页：来源旁排除按钮 + 操作反馈行 + 概要显示占用 MB

## 四、不做

按窗口标题排除、按内容正则排除、加密存储、SQLite。
