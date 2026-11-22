# 内存与流畅度

> 复核时间：2026-11-23
> 约束：**不削减功能、不降低解码尺寸、不牺牲交互质量**。
> 目标不是“内存永远最低”，而是：**有上限、能回落、可预测，并且越用越顺**。

---

## 一、结论

三阶段实测：

| 阶段 | Working Set | 增量 |
|---|---:|---:|
| 托盘空闲（未打开工作台） | **119 MB** | 基线 |
| 打开工作台（56 条剪贴板记录） | **231 MB** | **+112 MB** |
| 打开截图窗口 | **431 MB** | **+200 MB** |

关键判断：

1. **+112 MB 不是剪贴板图片。** 当时 56 条记录合计只有 1.1 MB，图片只有一张 0.1 KB。增量来自第一次加载 9 个插件、工作台视觉树，以及 Avalonia 的字体/图标/渲染表面。
2. **+200 MB 是截图窗口的正常峰值。** 冻结帧、稳定检测的临时帧、全屏叠加层、UI Automation 树同时存在；4K 下仅冻结帧 + `WriteableBitmap` 就是约 66 MB。
3. **真正的问题是“关闭后不回落”。** `WorkspaceWindow` 关闭只是 `Hide()`，`WorkspaceViewModel` 和插件程序集继续驻留；截图窗口关闭后 LOH 也不一定压缩。于是空闲内存停在用过的高位。
4. **不能靠砍功能解决。** 正确方向是：保留“可复用的热状态”，释放“可再生的派生状态”，并用池化、零拷贝、预热和增量加载让下一次更快。

---

## 二、三次实测的归因

### 2.1 119 MB：托盘空闲

包含：

- .NET 运行时 + Avalonia；
- 托盘、热键线程、剪贴板监听线程（后两者都阻塞在消息循环，CPU 近 0）；
- 已创建但未显示的工作台/面板窗口骨架；
- 剪贴板历史元数据（默认上限 200 条）。

这一层没有明显浪费；CPU 也基本为 0。问题不在“空闲线程”，在“用过之后不释放”。

### 2.2 +112 MB：第一次打开工作台

日志能对上：

```
00:02:42  发现插件 …（只读 manifest）
00:03:11  已加载插件 …（9 个）
```

主要组成：

| 项 | 说明 |
|---|---|
| 插件程序集 | 9 个插件第一次进入 `AssemblyLoadContext`，默认上下文不会卸载 |
| 工作台视觉树 | 工具墙、剪贴板页、详情页、筛选器、模板 |
| 剪贴板 VM | `ClipboardHistoryViewModel.Refresh()` 最多建 1000 个条目 VM |
| 渲染资源 | 字体、图标、卡片模板、Avalonia 渲染表面 |

当时剪贴板只有 1.1 MB，**缩略图缓存不是这一跳的原因**。

### 2.3 +200 MB：截图窗口

`CaptureWindow` 打开后同时存在：

- `_frozenPixels`：整屏 BGRA；
- `_frozenBitmap`：同一画面的 native 位图；
- `CaptureStableRaw` 稳定检测期间的 2 帧临时缓冲；
- 覆盖整个虚拟桌面的全屏窗口/渲染表面；
- `UiElementProbe` 的 UI Automation 树与 COM 对象。

以 4K 估算：

```
冻结 BGRA                 33 MB
WriteableBitmap           33 MB
稳定检测临时帧            66 MB（短暂）
全屏渲染表面              ~30 MB
UIA + 其他                ~20–50 MB
```

**所以 431 MB 本身不算异常峰值。** 是否合理，要看关闭截图后能不能回到 200 MB 左右。

### 2.4 关闭后不回落的原因

| 原因 | 位置 |
|---|---|
| 工作台关闭仅 `Hide()`，数据/视觉树全留 | `WorkspaceWindow.Closing` |
| 启动时就创建了 `WorkspaceWindow`，并立即 `ClipboardHistoryViewModel.Refresh()` | `App.axaml.cs`、`ClipboardHistoryViewModel` 构造函数 |
| 缩略图 VM 用 `Lazy<byte[]>` 缓存**完整 PNG** | `ClipboardEntryViewModel.Thumbnail` |
| `ImageCache` 是静态单例，没有关闭时清空入口 | `ImageCache.Instance` |
| 冻结帧释放后只在 WorkingSet ≥ 600 MB 时才压缩 LOH | `TrimFreezeMemory` |
| 工作区上次的输入图/结果图仍在 ViewModel 里 | `WorkspaceViewModel.InputImages / ResultSections / PreviewImagePng` |

---

## 三、原则：不砍功能，怎么优化

把内存分成两类，处理方式不同：

| 类型 | 例子 | 策略 |
|---|---|---|
| **热状态（保留）** | 插件程序集、工具注册表、字体/图标、最近记录元数据、当前视图缩略图 | 保留，但设字节/条数预算；跨会话预热 |
| **派生状态（可释放）** | 解码后的 Bitmap、完整 PNG 字节、输入图副本、UIA 树、冻结帧、滚动拼接缓冲 | 隐藏/关闭/空闲后释放，下次按需重建 |

三条原则：

1. **不为了省内存反复解码。** 解码一次、按字节 LRU 复用，比每次重新解码更流畅。
2. **释放“可再生”的东西，不释放“重新加载很贵”的东西。** 插件、模型、字体保留；原图字节和 Bitmap 可丢。
3. **避免 GC 风暴。** 用池化缓冲减少 LOH 分配；只在释放了大块内存或真正空闲时压缩，而不是每次关窗都 `GC.Collect`。

> “越用越流畅”不是“内存一直很小”，而是：
> **第一次打开慢一点，之后复用热状态；空闲时只丢派生数据，下一次操作不用重新做最贵的部分。**

---

## 四、优化清单

### A. 生命周期：懒创建 + 隐藏后释放派生状态

**现状**

- `App` 启动时创建 `WorkspaceWindow` / `PaletteWindow`；
- `WorkspaceViewModel` 构造时立即 `ClipboardHistoryViewModel.Refresh()`，还会选中第一条并 `LoadDetail()`；
- `WorkspaceWindow.Closing` 只 `Hide()`；
- `ImageCache` 没有 `Clear()`。

**做法**

1. 工作台/面板改为**首次打开时创建**；托盘菜单只读 manifest，不需要窗口实例。
2. `WorkspaceWindow` 打开时才 `Refresh()`；关闭时取消当前详情图。
3. 隐藏后加 **60 秒空闲释放**：
   - 清空 `Rows` / `Groups` / `Items` 中的图片 VM；
   - `DetailImagePng = null`；
   - `ImageCache.Instance.Clear()`（延迟一个 Dispatcher tick，先断开 `Image.Source`）；
   - 保留插件、工具注册表、最近记录文本和布局。
4. 再打开时按需重建，插件已热，第二次打开会比第一次快。

**预期**：关闭工作台后从 231 MB 回落到 **150–180 MB**；第二次打开不再重新加载插件。
**风险**：低；需要区分“面板和工作区共用同一个 Clipboard VM”的引用关系。

---

### B. 图片管线：零拷贝 + 按文件流式缩略图

**现状**

- `InputImageViewModel.PreviewBytes => Input.Data.ToArray()`：每次绑定复制整图；
- `TextSectionViewModel.ImagePng = sourceImage?.PreviewBytes`：每个结果段再复制一份；
- `ImageSizeReader.Read` 为了读宽高执行 `data.ToArray()`；
- `ClipboardEntryViewModel.Thumbnail` 用 `Lazy<byte[]>` 保留完整 PNG；
- `ByteArrayToBitmapConverter` 无参数分支每次 `DecodeOriginal`，不缓存也不 Dispose；
- 工作台若干预览 `<Image>` 没有 `ConverterParameter`。

**做法**

1. `PreviewBytes` 不再 `.ToArray()`：
   ```csharp
   MemoryMarshal.TryGetArray(Input.Data, out var segment);
   // 用 segment.Array + Offset + Count 构造只读 MemoryStream
   ```
   同一份字节只保留一份。
2. `ImageSizeReader` 用 `fixed` + `SKMemoryStream(ptr, length, false)`，只读文件头，不复制整图。
3. 剪贴板缩略图改为 `ImageCache.GetThumbnailFromFile(path)`：
   - 直接 `File.OpenRead` + `Bitmap.DecodeToHeight(240)`；
   - 缓存只保留 240px 的 Bitmap，不保留原 PNG；
   - 详情/灯箱需要原图时再读文件。
4. 固定尺寸预览统一用 `ConverterParameter=detail`（800px LRU），不要用无参数原尺寸分支。
5. 需要原尺寸 Bitmap 的地方，在 `Source` 变化时 Dispose 旧 Bitmap；或改成自定义控件管理生命周期。

**预期**：50 张输入图时减少 100–400 MB 的瞬时复制；滚动历史不再把原图字节越积越多。
**风险**：中；需要逐个检查绑定和转换器调用点。

---

### C. 截图缓冲：池化 + 单份冻结帧

**现状**

- `_frozenPixels` 和 `_frozenBitmap` 同时存在；
- `CaptureStableRaw` 每次新建/交换整屏数组；
- 关窗只在大 WorkingSet 时压缩 LOH。

**做法**

1. 冻结帧使用 `ArrayPool<byte>.Shared.Rent`（或窗口私有的可复用缓冲），关窗归还；
2. 颜色采样如果只需要少量像素，改为从 `WriteableBitmap` 按点读取，创建位图后即可释放 `_frozenPixels`，省一份整屏；
3. 稳定检测的两帧也走池化，避免 LOH 反复分配；
4. `OnClosed` 先 `FrozenFrame.Source = null`，再 Dispose 位图，最后归还缓冲；
5. LOH 压缩条件改为“本次释放的冻结缓冲 > 16 MB”或“应用空闲 5 秒”，而不是固定 600 MB；
6. 不在每次关窗都 `GC.Collect`，避免下一次截图变慢。

**预期**：4K 截图峰值从约 430 MB 降到 **330–380 MB**；关闭后回落更快。
**风险**：中；池化缓冲要注意生命周期，避免把已归还数组继续使用。

---

### D. UI Automation：按需建树、及时释放

**现状**

- 每次截图窗口创建 `UiElementProbe`，按窗口缓存最多 12000 个节点；
- 当前实现会淘汰其他窗口的树，但整个截图会话内仍保留当前窗口的树。

**做法**

1. 首次鼠标移动才初始化 UIA（已经接近这样），不要一开窗就建树；
2. 选区确定、开始标注或打开动作栏后，清空 UIA 树；
3. 截图关闭时 `Dispose`（已有）；
4. 对超大窗口继续用 12000 节点上限；命中失败直接退回窗口级。

**预期**：截图峰值减少 20–50 MB；关闭后 COM 对象及时释放。
**风险**：低。

---

### E. 剪贴板历史：分页 + 增量，而不是一次 1000 条

**现状**

- `MaxLoad = 1000`；
- 每次 `Refresh()` 清空再重建；
- 收起不释放；
- 图片 VM 的 `Lazy` 保留完整 PNG。

**做法**

1. 首屏只加载 100 条，滚动到底再加载下一页；
2. `Refresh()` 做 diff：保留相同 id 的 VM，只增删变化项，避免整列表重建；
3. 缩略图只保留 240px Bitmap（见 B）；
4. 隐藏 60 秒后释放 `Rows` 和图片 VM，保留文本元数据；
5. `_deleted` 仍保留 20 条撤销，但只存文件路径/按字节限流，不减少撤销层数。

**预期**：工作台打开/关闭的内存和时间都更平稳；滚动历史不再线性增长。
**风险**：低–中；分页和 diff 要处理选中项/滚动位置。

---

### F. OCR / 翻译：热池 + 空闲超时

**现状**

- OCR 每次 `await using` 启动 worker，模型初始化 1–3 秒，结束杀进程；
- 本地翻译每次加载 1.1 GB / 4 GB 模型，用完释放；
- 好处是空闲不占内存，坏处是每次都很慢。

**做法（不砍功能，提升“越用越流畅”）**

1. OCR worker 改成**小池 + 空闲超时**（例如保留 1 个进程 90 秒）：
   - 连续识别/翻译时复用模型，第二次开始明显更快；
   - 超时或内存压力时退出，空闲仍能回落。
2. 本地翻译同理：保留最近一次模型 60–120 秒；切换模型或超时才释放。
3. 池大小、超时时间写入设置，默认“平衡”：空闲不常驻大模型，但短时间连续操作不重复加载。

**预期**：首次 OCR/翻译仍然慢；第二次开始快很多；空闲内存有上限。
**风险**：中；需要处理进程崩溃、模型切换、内存压力。

---

### G. 滚动截图：分块 + 落盘，不截断

**现状**

- `ScrollStitcher._rows` 无限累积；
- 长页面可能几百 MB，`BuildPng` 再分配整张位图。

**做法**

1. 用分块缓冲（chunk），内存只保留当前块和模板哈希；已拼好的块追加到临时文件；
2. 结束时用内存映射文件承载整张位图（或流式 PNG 编码），避免再复制一份完整 `SKBitmap`；
3. `BuildPng` 完成后删除临时文件，并 `finally` 释放块缓冲；
4. 进度显示“已拼接行数”；磁盘不足时明确报错，**不静默截断内容**。

**预期**：滚动截图的内存由块大小决定，不随页面长度线性增长；长页面仍然完整。
**风险**：中；需要处理临时文件清理、磁盘空间和编码失败回退。

---

### H. GC 与缓存预算

- 解码 Bitmap 是 native 内存，GC 不知道；继续用**按字节 LRU**，不要按条数。
- 淘汰不立即 Dispose（避免控件还在渲染），但要在窗口隐藏/空闲时统一 `Clear()`；先断开 `Source`，再 Dispose。
- 给缓存定预算：
  ```
  缩略图 ≤ 16 MB
  详情   ≤ 8 MB
  图标   ≤ 4 MB
  ```
- 监控 `WorkingSet`、`GC.GetTotalMemory(false)`、`ImageCache.Stats()`，在状态切换时写日志。
- 只在“释放了大块缓冲”或“应用空闲”时做 LOH 压缩，避免每次关窗都 GC 导致下一次操作卡顿。

---

## 五、“越用越流畅”的目标模型

| 阶段 | 目标内存 | 目标体验 |
|---|---:|---|
| 冷启动 | ~120 MB | 托盘就绪 |
| 第一次打开工作台 | 200–240 MB | 加载插件（一次性） |
| 关闭工作台后 60 秒 | 140–180 MB | 保留插件/元数据，释放图片派生状态 |
| 第二次打开工作台 | 160–200 MB | 插件已热，明显快于第一次 |
| 第一次截图 | 330–430 MB（随分辨率） | 加载 UIA/全屏表面 |
| 关闭截图后 10 秒 | 回到此前基线 +10% | 冻结帧/缓冲释放 |
| 第二次截图 | 300–380 MB | UIA/Skia 已热，开窗更快 |
| 连续 OCR | 进程池内 1 个 worker | 第二次识别不再等模型 |
| 空闲 2 分钟 | 关闭 worker/大模型 | 内存回落 |

判断标准不是“数字最小”，而是：

1. **不随使用次数单调上涨**；
2. **关闭窗口后能回落到可接受区间**；
3. **重复操作越来越快**（插件、缩略图、UIA、OCR 模型复用）；
4. **峰值由屏幕分辨率和当前任务决定，不由历史操作次数决定**。

---

## 六、实施顺序

| 阶段 | 内容 | 预期 | 风险 |
|---|---|---|---|
| P0 ✅ | 工作台/面板懒创建；隐藏 60 秒释放派生状态；`ImageCache.Clear()` | 空闲回落 30–80 MB | 低 |
| P0 ✅ | 截图关闭按缓冲大小触发 LOH 压缩；先断 Source 再 Dispose | 截图后回落更快 | 低 |
| P1 ✅ | `PreviewBytes` / `ImageSizeReader` 零拷贝；缩略图按文件流式解码 | 输入图瞬时峰值大幅下降 | 中 |
| P1 ✅ | 工作台预览统一走 preview 缓存（1600px）；图标保持原尺寸（≤32px） | 切换图片不再解 4K 原图 | 低 |
| P2 ✅ | 历史分页 + diff 刷新；`_deleted` 降上限 | 打开/滚动更平稳 | 中 |
| P2 ✅ | UIA 建树时机与释放 | 截图峰值 -20–50 MB | 低 |
| P3 | OCR/翻译热池 + 空闲超时（可配置） | 连续操作更快，空闲仍回落 | 中 |
| P3 | `ArrayPool` 截图缓冲；滚动拼接分块落盘 | LOH 更平稳 | 中 |
| P4 | 状态切换日志与预算测试 | 防止回归 | 低 |

原则：**先做“释放”和“零拷贝”，再做“池化/热池”**。先保证不涨，再追求更快。

---

## 七、复测方法

### 7.1 六个时点

1. 冷启动，等 10 秒；
2. 打开工作台，等 10 秒；
3. 关闭工作台，等 60 秒；
4. 打开截图窗口，等 5 秒；
5. Esc 关闭截图，等 10 秒；
6. 再打开截图，鼠标在窗口上缓慢移动 10 秒。

每一步记录：

```powershell
Get-Process Sparrow.App | Select-Object WorkingSet64, PrivateMemorySize64
```

日志补一行：

```csharp
App.Log.Info(
    $"内存：WorkingSet={Environment.WorkingSet / 1048576}MB, " +
    $"Managed={GC.GetTotalMemory(false) / 1048576}MB");
```

### 7.2 更细的工具

```powershell
dotnet-counters monitor -p <pid> System.Runtime
dotnet-gcdump collect -p <pid>
```

对比“关闭工作台前 / 后”的 gcdump，重点看：

- `ClipboardEntryViewModel`
- `Avalonia.Media.Imaging.Bitmap`
- `byte[]`
- `Svg.Skia` / `SKBitmap`

### 7.3 验收线

- 关闭工作台后 60 秒：不高于空闲基线 +60 MB；
- 关闭截图后 10 秒：不高于关闭前 +10%；
- 连续两次打开同一工作台：第二次不高于第一次；
- 连续 OCR 两次：第二次耗时明显低于第一次，且空闲 2 分钟后内存回落。

---

## 八、功能与交互影响

结论：**功能清单不变，快捷键不变，标注/选区交互不变。**
变化主要集中在“第一次打开要等多久”“关闭后多久释放”“重复操作有多快”。

### 8.1 完全无感

| 改动 | 用户能否感知 |
|---|---|
| 冻结帧/截图缓冲池化 | 否 |
| 关闭截图时后台 LOH 压缩 | 否（最多一次短暂后台 CPU） |
| `PreviewBytes` / `ImageSizeReader` 零拷贝 | 否，画面一样 |
| 缩略图按文件流式解码 | 首次略慢，之后走 LRU；画面一样 |
| `ImageCache.Clear()` | 否，下次打开重新解码 |
| 工作台刷新做 diff | 否，列表内容一样 |
| UIA 树在选区确定后释放 | 否，候选窗口只在框选前需要 |
| 历史删除撤销改为存路径/按字节限流 | 否，撤销仍然可用 |

### 8.2 有轻微时序变化

| 改动 | 变化 | 是否影响功能 |
|---|---|---|
| 工作台/面板懒创建 | 第一次打开多 100–300 ms | 不影响 |
| 隐藏 60 秒后释放派生数据 | 60 秒内重开保持原样；超过后重开重新加载缩略图/详情 | 不影响，列表内容仍在 |
| 历史首屏 100 条 + 滚动加载 | 打开更快，滚到很旧记录时才加载 | 不影响 |
| OCR/翻译热池 | 空闲 60–120 秒内进程/模型常驻，内存较高；再次操作明显更快 | 不影响，结果一样 |

### 8.3 需要特别守住的三条

1. **不自动清空用户正在编辑的任务**
   - `InputImages`、`ResultSections`、当前工具参数不能因为“隐藏 60 秒”直接丢。
   - 要释放就把字节落到临时文件，重开时恢复；或者只释放 Bitmap/缓存，不释放逻辑数据。
2. **滚动截图不设“截断式上限”**
   - 直接到 256 MB 就停止会改变功能。
   - 正确做法是分块 + 落临时文件 + 结束时流式编码，内存有界但不丢内容。
3. **删除撤销不降层数**
   - `_deleted` 从 20 张降到 5 张是功能缩水；应改成“保留 20 条但只存文件路径/按字节限流”。

### 8.4 用户真正会感觉到的变化

- 第一次打开工作台：可能略慢；之后因为插件已热，明显更快。
- 关闭工作台再开：列表秒开，图片按需重新解码。
- 连续 OCR/翻译：第二次开始不用再等模型，明显更流畅。
- 连续截图：第二次开窗更快（UIA/Skia 已热）。
- 空闲内存：关掉窗口后能回落，不再“用一次涨一次”。

---

## 九、历史记录（2026-11-21）

> 构建：Release（net10.0）　机器：Windows 10 19045 / 16 逻辑核
> 数据：真实剪贴板历史（75 张图片，磁盘 7.4 MB）

| 时点 | 改造前 | 虚拟化后 |
|---|---:|---:|
| 冷启动完成 | 120.2 MB | 119.9 MB |
| 首次打开面板（75 张卡片） | 388.4 MB | **211.6 MB** |
| Esc 收起 | 388.8 MB | 212.0 MB |
| 再次打开 | 502.9 MB | **220.7 MB** |
| 再次收起 | 498.1 MB | 220.7 MB |
| 缩略图缓存 | 37 张 / 15 MB | 2 张 / 0 MB（仅视口） |

当时结论：

- 解码尺寸已经解决（`DecodeToHeight(240)`，平均 405 KB/张）；
- 主要开销是列表未虚拟化，75 张卡片全部实例化；
- 收起只是 `Hide()`，重开继续涨；
- 改成 `Rows` + `VirtualizingStackPanel` 后，打开从 388 → 212 MB，重开从 +115 MB → +9 MB。

这次 2026-11-23 的 119 / 231 / 431 说明：**虚拟化仍然有效，但工作台生命周期、截图冻结帧和图片派生状态还没收口。**

---

## 十、落地记录

### P0（已完成）

- **工作台窗口懒创建**：`ShowWorkspace()` 第一次打开时才创建 `WorkspaceWindow`；启动只保留 `WorkspaceViewModel`。
- **启动不刷新剪贴板**：`WorkspaceViewModel` 用 `loadClipboardOnCreate: false` 创建 `ClipboardHistoryViewModel`；首次进入剪贴板页/面板才 `Refresh()`。
- **隐藏 60 秒释放派生状态**：
  - `WorkspaceWindow.Closing` 安排 `App.ScheduleWorkspaceRelease()`；
  - 60 秒内重开、或剪贴板面板可见时跳过；
  - `WorkspaceViewModel.ReleaseTransientState()` 清空剪贴板 `Items/Groups/Rows`、详情图和 `ImageCache`；
  - 输入图、结果、历史、顺序粘贴队列、删除撤销保留；
  - 释放后再次打开：如果停在剪贴板页会 `Refresh()`，并恢复之前选中的条目 id。
- **后台不再重建隐藏列表**：`ClipboardHistoryViewModel` 释放后收到剪贴板变化只标记，不 `Refresh()`；下次打开再读。
- **截图关闭回落**：
  - `OnClosed` 先 `FrozenFrame.Source = null`，再 Dispose `WriteableBitmap`；
  - 按本次释放的冻结帧大小触发 LOH 压缩，不再只看 WorkingSet ≥ 600 MB；
  - 小屏/测试不付 GC 成本。

### 验收

- 构建：0 warning / 0 error；
- 测试：**689 通过**；
- 新增：
  - `ImageCache.Clear()` 统计归零；
  - `ClipboardHistoryViewModel` 延迟刷新；
  - 释放派生状态后 `Refresh()` 恢复选中项。

### P1（已完成）

- **输入图零拷贝**：`InputImageViewModel.PreviewBytes` 在底层是完整数组时直接复用，不再每次 `ToArray()`；`TextSectionViewModel`、`WorkspaceViewModel` 共享同一份字节。
- **图片头读取不复制整图**：`ImageSizeReader.Read(ReadOnlyMemory<byte>)` 对完整数组走 `SKMemoryStream`，不再显式 `ToArray`。
- **剪贴板缩略图按文件流式解码**：
  - `ClipboardHistoryStore.ImagePath(id)` 只按 id 查 `image_file`；
  - `ClipboardEntryViewModel.ThumbnailPath` 只缓存路径（懒查询一次），不保留完整 PNG；
  - `ImageCache.GetThumbnailFromFile(path)` 直接 `File.OpenRead` + `DecodeToHeight(240)`，按路径/长度/最后写入时间做 LRU；
  - 文件条目、文本条目行为不变。
- **预览缓存分层**：
  - 缩略图 ≤ 16 MB；
  - 详情 ≤ 8 MB；
  - 新增 **preview（1600px 宽）≤ 16 MB**，用于输入图/结果图/SVG 预览；
  - 工作台里原来的无参数原尺寸解码全部改走 `ConverterParameter=preview`，切换图片不再每次解 4K 原图。
- **缓存日志**补上 preview 条目和字节数。

### 验收（P1 后）

- 构建：0 warning / 0 error；
- 测试：**693 通过**；
- 新增：
  - 按文件解码缩略图并命中缓存；
  - preview 按 1600px 宽降采样；
  - 输入图 `PreviewBytes` 复用底层数组；
  - 图片条目暴露缩略图路径。

### P2（已完成）

- **历史首屏分页**：
  - 首屏只读 100 条（`PageSize = 100`，上限仍是 1000）；
  - 列表底部「加载更多」每次再展开 100 条，已加载条目原样保留；
  - 搜索/筛选/切换页面会重置到第一页；
  - 状态栏摘要显示「共 N 条 · 已显示 M 条」，避免误以为记录变少。
- **diff 刷新**：
  - `Refresh()` 不再整列表重建 VM，按 id 复用内容未变的条目；
  - 只有新增、内容变化、删除的条目才创建/替换 VM；
  - 剪贴板监听线程触发的刷新保持当前页深，不会把「加载更多」吞回去。
- **UIA 提前释放**：
  - `UiElementProbe.ReleaseTrees()` 把缓存的控件树投递到 UIA 线程释放，保留 COM 自动化对象；
  - 选区确定（拖拽完成、单击候选）后立刻释放；
  - 进入 OCR 时再次释放；
  - 截图窗口关闭仍走原来的 `Dispose()`，两者共用同一段清理逻辑。

### 验收（P2 后）

- 构建：0 warning / 0 error；
- 测试：**695 通过**；
- 新增：
  - 首屏只加载一页，`LoadMore()` 后补齐；
  - 内容未变的条目刷新后复用同一个 VM 实例。

### 还没做（P3+）

- OCR/翻译热池（可配置性能优先/内存优先）；
- 滚动截图分块落盘；
- 缓存淘汰后的延迟 Dispose / 自动生命周期。
