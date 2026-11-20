# 内存实测记录

> 复核时间：2026-11-20　构建：Release（net10.0）　机器：Windows 10 19045 / 16 逻辑核
> 数据源：真实剪贴板历史（75 张图片，磁盘共 7.4 MB），没有造数据

## 一、场景

| 项 | 值 |
|---|---|
| 历史条目 | 75 张图片（磁盘 7.4 MB，单张最大 778 KB） |
| 操作 | 冷启动 → 全局热键打开剪贴板面板 → Esc 收起 → 再打开 → 再收起 |
| 测量 | `Process.WorkingSet64` / `PrivateMemorySize64`，另用 `dotnet-counters`、`dotnet-gcdump` 交叉验证 |

## 二、数据

| 时点 | 工作集 |
|---|---:|
| 冷启动完成 | 120.2 MB |
| 首次打开面板（渲染 75 张卡片） | 388.4 MB |
| Esc 收起 | 388.8 MB（**不回落**） |
| 再次打开 | 502.9 MB |
| 再次收起 | 498.1 MB |
| 强制 GC 之后 | 478.8 MB（私有 405.1 MB） |

进程内计数（同一次运行）：

```
缩略图缓存：37 张 / 15 MB   （按字节 LRU，上限 16 MB —— 工作正常）
详情缓存：  0 张 / 0 MB
托管堆：    gen2 209 MB + LOH 17 MB，committed 241 MB
gcdump：    GC heap 135 MB（lossy，值偏小）
```

## 三、结论

**1. 解码尺寸的问题已经解决。** 缓存里 37 张缩略图共 15 MB，平均 405 KB/张，
对应 427×240×4 —— 说明 `DecodeToHeight(240)` 真的在解码阶段缩了，
不再是 3 张 4K 就占 95 MB 的老样子。

**2. 剩下的内存不在解码，在列表结构。** 面板是
`ScrollViewer > ItemsControl(Groups) > ItemsControl(Items) > 卡片`，
**没有虚拟化**：75 张卡片会全部实例化，每张卡片的视觉树、位图纹理、文本布局都常驻。
按 268 MB / 75 张 ≈ 3.5 MB/张算，单张缩略图（0.4 MB）解释不了这个量级。

**3. 收起面板不释放，重开继续涨。** `HidePanel` 只是 `Hide()`，视觉树留着；
每次呼出又 `Refresh()` 全量重建 75 个条目。这就是「关掉不回落 + 每次 +115 MB」的来源。
`ImageCache` 有上限兜底，所以增长不是无界的，但高水位一次比一次高。

## 四、下一步（按性价比）

1. **面板列表虚拟化**：`ListBox`（分组用 `GroupStyle`）或自建按视口回收的列表。
   这是唯一能同时解决「高水位」和「重开上涨」的改法。
2. **收起时释放**：`HidePanel` 里清空渲染（例如把 `ItemsSource` 换成空集合，
   下次呼出再 `Refresh`），让空闲内存回到 120 MB 量级。
   注意面板与工作区共用同一个 `ClipboardHistoryViewModel`，清空要能区分「给谁看」。
3. **别再加解码参数**：240 / 800 这两个数字是对的，继续调没有收益。

## 五、怎么复测

```powershell
# 1. 启动 Release 版
Start-Process src\Sparrow.App\bin\Release\net10.0\Sparrow.App.exe
# 2. 用 SendInput 注入 Ctrl+Alt+B（RegisterHotKey 全局生效，不需要焦点）
# 3. 每次操作后读 WorkingSet64，并看日志里的「图片缓存：…」
Get-Content "$env:LOCALAPPDATA\Sparrow\logs\sparrow-*.log" | Select-String '图片缓存' | Select-Object -Last 2
```

日志里的缓存统计由 `ClipboardPanelWindow.HidePanel` 写出，专门为这种复核留的。
