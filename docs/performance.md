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

> 2026-11-21 回归复核（标注 / 吸附 / SDK 1.9 之后）：冷启动中位 **946 ms**（5 次），
> 空载 **121.3 MB**、打开面板 **217.5 MB**、重开 **232.4 MB**（+15 MB），仍无回落。
> 与下面虚拟化后的数字一致，标注层和吸附层没有带回量级增长。

| 时点 | 改造前 | 改造后（虚拟化） |
|---|---:|---:|
| 冷启动完成 | 120.2 MB | 119.9 MB |
| 首次打开面板（75 张卡片） | 388.4 MB | **211.6 MB** |
| Esc 收起 | 388.8 MB | 212.0 MB |
| 再次打开 | 502.9 MB | **220.7 MB** |
| 再次收起 | 498.1 MB | 220.7 MB |
| 缩略图缓存 | 37 张 / 15 MB | 2 张 / 0 MB（只实现视口内的行） |

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

**2. 主要开销来自「列表未虚拟化」。** 面板原来是
`ScrollViewer > ItemsControl(Groups) > ItemsControl(Items) > 卡片`，
75 张卡片全部实例化，每张的视觉树、位图纹理、文本布局都常驻。
按 268 MB / 75 张 ≈ 3.5 MB/张算，单张缩略图（0.4 MB）解释不了这个量级。

**3. 收起面板不释放，重开继续涨。** `HidePanel` 只是 `Hide()`，视觉树留着；
每次呼出又 `Refresh()` 全量重建 75 个条目 —— 这就是「关掉不回落 + 每次 +115 MB」。

**4. 改成扁平虚拟化后，两个问题一起消失。** `ClipboardHistoryViewModel.Rows`
把「分组头 + 条目」拍平成一个集合，面板和工作区都用
`ItemsControl + VirtualizingStackPanel` 绑定它：

```
打开：388 → 212 MB（-45%）
重开：+115 MB → +9 MB
缩略图：37 张 → 2 张（只解码可见行）
```

## 四、下一步（按性价比）

1. ~~面板列表虚拟化~~ ✅ 已完成（面板 + 工作区剪贴板页）。
2. **收起时清空**（可选）：现在收起后仍保留视口内那十几张卡片（≈ 10 MB）。
   要继续压，可以在 `HidePanel` 里把 `Rows` 换空集合、下次呼出再建。
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
