# 2026-11-18 剪贴板交付能力：拖出文件 + 顺序粘贴

> 状态：已实现

## 一、来源

参考（只借鉴设计，未拷代码）：

- **CopyAttach**：粘贴队列（连续标记，按序粘贴）、撤销删除、来源应用搜索
- **ClipLine**：卡片拖出为 `.txt/.md` / 文件；按时间分叠
- **TieZ**：顺序粘贴、标签、隐私遮罩
- **SyncClipboard**：带标签记录免清理、保留策略（同步本身不做，违反本地优先）

## 二、范围

这一轮只做「把内容送出去」：

1. **拖出为真实文件**
   - 文本 → 临时 `.txt`
   - 图片 → 临时 `.png`
   - 文件条目 → 原路径文件
   - 临时目录 `%TEMP%\Sparrow\drag`，启动时清理一天前的残留
2. **顺序粘贴队列**
   - 行内「＋」或 Ctrl+点击加入；队列条显示编号与预览
   - 「复制下一条」只写剪贴板，零输入注入
   - 全局热键（默认 `Ctrl+Alt+V`）：写入下一条并模拟 `Ctrl+V`
   - 粘贴来源是宿主主动写剪贴板，监听器需临时抑制，否则历史会被粘贴内容刷屏

## 三、实现

- `ClipboardHistoryViewModel`：队列、编号、取出下一条、拖出载荷
- `ClipboardPasteService`：抑制历史 → 写剪贴板 →（可选）注入按键
- `PasteInjector`：先松开热键里的 Alt/Shift/Win，再发 Ctrl+V，避免再次命中自己的热键
- `ClipboardDragService`：临时文件 + `DataTransfer` 文件项
- `ClipboardMonitor.SuppressNextWrite`：写剪贴板后 800ms 内不记历史
- `AppSettings.QueuePasteHotkey`：默认 `Ctrl+Alt+V`，可在 `settings.json` 改

## 四、不做

跨设备同步、撤销删除、时间分组、来源应用筛选、敏感内容遮罩——留到下一轮。
