# Sparrow 工具界面内存 / 性能审查报告

- 审查日期：2026-11-26
- 审查范围：`src/Sparrow.App`（截屏 `CaptureWindow.*`、录屏 `Recording*`、工具条 `ToolCatalog`/`ToolRunner`、图片相关窗口），排除 `bin/obj`
- 主用技能：`analyzing-dotnet-performance`（scan depth = standard）
- 参考覆盖：`critical-patterns.md`、`memory-and-strings.md`、`structural-patterns.md`、`async-patterns.md`、`collections-and-linq.md`、`io-and-serialization.md`、`regex-patterns.md`（7/7 全部可用，无降级）
- 扩展检查：IDisposable / 事件订阅 / DispatcherTimer / Bitmap native 内存 / WGC 帧缓冲生命周期（skill 规则本身不覆盖，单独标注）

---

## 一、结论速览

**有，而且不止一处。** 项目里已经做过一轮相当扎实的内存治理（`ImageCache` 按字节 LRU、冻结帧关窗释放 + LOH 压缩、`PinnedImageWindow`/`ClipboardImageZoomWindow`/`ScrollResultWindow` 都在 `OnClosed` 里 `Dispose()`、工作台隐藏 60 秒释放），所以常规路径是干净的。剩余问题集中在**「中途关窗 / 启动失败」这类非正常退出路径**上，恰好就是"打开截屏、录屏界面后反复开关，内存不回落"的表现。

| 编号 | 严重度 | 问题 | 每次触发的代价 |
|---|---|---|---|
| F1 | 🔴 | `CaptureWindow.OnClosed` 没停两个 `DispatcherTimer` | 整个窗口 + 4K 冻结帧约 66 MB 永久驻留 |
| F2 | 🔴 | `_activeCaptureWindow` 在录屏启动失败时永不清除 | 同上，并导致录屏热键永久失效 |
| F3 | 🔴 | `WaitForClosedAsync` 无解绑 / 无超时，`async void` 状态机悬挂 | 冻结帧与结果 PNG 无法回收 |
| F4 | 🔴 | 录屏 `Completed` 回调在 UI 线程 `Dispose()` | UI 冻结最多 3 秒，异常路径资源延迟释放 |
| F5 | 🟡 | 水印编辑器位图从不释放 | 每次换图 / 开关窗口一张 native 位图等 finalizer |
| F6 | 🟡 | `ImageCache.Clear()` 只挂在工作台隐藏 60 秒 | 工具窗口关闭后约 40 MB 缓存 + finalizer 队列高位不回落 |
| F7 | 🟡 | WGC 帧缓冲 33 MB LOH 在失败路径常驻 | 录屏失败后单块大对象不释放 |

**本次没有改任何代码**，只做审查。修复优先级建议见第六节。

---

## 二、内存泄漏专项（扩展检查，skill 规则之外）

### 🔴 F1. `CaptureWindow.OnClosed` 未停止 `_recordTimer` 与 `_groupCloseTimer`（2 个定时器）

**Impact:** `DispatcherTimer` 由 Avalonia 全局 Dispatcher 持有，其 `Tick` lambda 捕获 `this`，窗口关闭后仍按 200 ms / 120 ms 持续触发，形成 `Dispatcher → Timer → Tick → CaptureWindow` 的强引用链，整个窗口（含 `_frozenPixels` 33 MB byte[] + `_frozenBitmap` 33 MB native 位图 + 完整视觉树）永不回收。

**根因链：**
- `Views/CaptureWindow.Recording.cs:122-130` 创建 `_recordTimer`，Tick 内引用 `_recording?.Elapsed`、`UpdateRecordingWindowRegion()`
- `Views/CaptureWindow.Toolbar.cs:526-527` 创建 `_groupCloseTimer`
- 停止路径会停：`Recording.cs:165`（正常停止）、`:201`（取消）、`Toolbar.cs:550`（`HideToolGroupPopover`）
- **但 `Views/CaptureWindow.axaml.cs:1027` 的 `OnClosed` 只写了 `_groupShowTimer?.Stop()`**
- 录制中按 Esc / 直接关闭窗口 → `OnClosed` 走 `_recording.Abandon()` 分支（`:1004-1010`），不清 `_recordTimer`、不清 `_groupCloseTimer`，也不解绑 Tick

`OnClosed` 里那些 `Dispose()` 因此全部白做：位图对象被释放了，但持有它的窗口还活着，`TrimFreezeMemory()` 触发的昂贵 full GC 也收不回来。

**Files:** `Views/CaptureWindow.axaml.cs:1001-1043`, `Views/CaptureWindow.Recording.cs:122,130,165,201`, `Views/CaptureWindow.Toolbar.cs:207-211,526-531,550`

**Fix:** 在 `OnClosed` 补齐 `_recordTimer?.Stop(); _recordTimer = null; _groupCloseTimer?.Stop(); _groupCloseTimer = null; _groupShowTimer?.Stop(); _groupShowTimer = null;`，并在置空前 `Tick -=` 断开委托；`DispatcherTimer` 不建议复用，直接置 null 让 GC 收掉最稳。

---

### 🔴 F2. `_activeCaptureWindow` 在录屏启动失败时成为永久悬挂引用（1 处）

**Impact:** App 是应用级单例，字段一旦不清空，指向的 `CaptureWindow` 及其整屏冻结帧就永久驻留；同时造成功能死锁——`ToggleScreenRecording()` 永远走"停止录制"分支，`RecordingSessionStarter` 永远返回 null 并提示"已经在录制中"。

**根因链：**
- `App.Capture.cs:161` 在 `RecordingSessionStarter` 里**先**赋值 `_activeCaptureWindow = capture`，**再** `CreateRecordingSessionAsync(...)`
- 唯一清除点：`App.Capture.cs:187` 的 `RecordingFinished` 回调
- `RecordingFinished` 只有 3 个 Invoke 点：`CaptureWindow.Recording.cs:172`（正常停止）、`:208`（取消）、`CaptureWindow.axaml.cs:1009`（关窗且 `_recording is not null`）
- 而 `CreateRecordingSessionAsync` 有 3 条返回 null 的路径：ffmpeg 下载取消（`App.Record.cs:211`）、倒计时取消（`:229`）、`TryStart` 失败（`:281`）
- 这些路径下 `StartRecordingAsync` 在 `session is null` 直接 `return`（`Recording.cs:76-77`），`_recording` 从未赋值 → 关窗时 `OnClosed` 的 `if (_recording is not null)` 不成立 → **不触发 `RecordingFinished`** → 字段永不清除

**Files:** `App.Capture.cs:152-165,185-187`, `App.Record.cs:20,25,211,229,281`, `Views/CaptureWindow.Recording.cs:74-80`, `Views/CaptureWindow.axaml.cs:1004-1010`

**Fix:** 二选一（建议都做）：① 把赋值移到 `CreateRecordingSessionAsync` 成功返回之后；② 在 `App.Capture.cs:246` 的 `await WaitForClosedAsync(capture)` 之后无条件 `_activeCaptureWindow = null`（用 `try/finally` 包裹，保证异常也清）。

---

### 🔴 F3. `WaitForClosedAsync` 悬挂状态机（2 处调用）

**Impact:** 窗口若未触发 `Closed`（异常销毁、被 `Hide` 而非 `Close`、退出路径异常），`TaskCompletionSource` 永不完成，`async void` 状态机的字段会永久持有 `capture` / `prewarmedProbe` / `png`，冻结帧和滚动截图结果 PNG 无法回收。

**Files:** `App.Pipelines.cs:386-391`（定义）、`App.Capture.cs:246`（`async void CaptureAndRecognize`）、`App.Pipelines.cs:170`（`ScrollResultWindow`）

**Fix:** 改成 `var handler = ...; window.Closed += handler; await tcs.Task; window.Closed -= handler;`，并给 `TaskCompletionSource` 加 `RunContinuationsAsynchronously`；`async void` 入口用 `try/finally` 兜底清理 `_activeCaptureWindow`、`prewarmedProbe`。

**Caveat:** 这是悬挂而非订阅泄漏——订阅者是 lambda 捕获的 `completion`，与窗口同生命周期；真正的问题是"状态机不结束"。

---

### 🔴 F4. 录屏 `Completed` 回调在 UI 线程执行 `session.Dispose()`（1 处）

**Impact:** `Dispose()` 内含 `_worker.Wait(3s)` 与 `_encoder.Dispose()`，在 UI 线程执行会造成最多 3 秒界面冻结；`Finished` 回调里 `_recordingBar = null` + `bar.Close()` 早于 `Closed` 事件的 `-=` 解绑；超时/失败路径下 `Dispose` 可能不被调用，WGC session、frame pool、D3D surface、ffmpeg 进程句柄与 33 MB 帧缓冲延迟释放。

**根因链：** `RecordingSession.Run():432` 在 worker 线程 `SetState(Completed)` → `RecordingBarWindow.OnStateChanged:158` → `Dispatcher.UIThread.Post` → `FinishWith:169-181` → `Finished?.Invoke` → `App.Record.cs:163-164` 在 UI 线程 `session.Dispose()`。同时 `StopAsync():228` 自己也在 `await _worker.WaitAsync(5s)`，两条收尾路径并发。

**Files:** `Services/Recording/RecordingSession.cs:215-249,342-451,497-527`, `Overlays/RecordingBarWindow.axaml.cs:134-181`, `App.Record.cs:161-188`

**Fix:** `Finished` 只发信号，把 `session.Dispose()` 移到 `Task.Run` 后台执行（或统一由 `StopAsync` 的调用方在 `finally` 收尾）；`Closed` 事件只做 `_session.StateChanged -= OnStateChanged` 解绑，不做释放。

**Caveat:** 与 F2 联动——区域录制走的是 `CaptureWindow.StopRecordingAsync`（`Recording.cs:143-173`），那条路径自己管理生命周期，不受此问题影响；本条针对**全屏/窗口录制 + 独立控制条**路径。

---

### 🟡 F5. `WatermarkEditorWindow` 位图从不释放（2 处 `new Bitmap`）

**Impact:** `Avalonia.Media.Imaging.Bitmap` 是 native 内存，GC 看不见其真实占用，只能等 finalizer；录屏设置里的水印编辑器每换一张图、每开关一次窗口，旧位图（4K 约 33 MB）就留在 finalizer 队列里，工作集只涨不跌。

**Files:** `Overlays/WatermarkEditorWindow.axaml.cs:62,68,75,176,189`（全文件 **0 个 `OnClosed`、0 个 `Dispose`**）

**Fix:** 覆盖 `OnClosed` → `WatermarkImage.Source = null` 后 `Dispose()`；替换 `Source` 前先取出旧 `Bitmap` 并 `Dispose()`；`OnClearImage` 同理。

---

### 🟡 F6. `ImageCache.Clear()` 触发面过窄（1 处）

**Impact:** 缓存有硬上限（16 + 8 + 16 = 40 MB），不构成严格泄漏，但 `Clear()` 明确"不主动 Dispose，交给 finalizer"（`ImageCache.cs:233-252`），关掉工具窗口后这 40 MB 加上 finalizer 队列里的旧位图不会立刻回落，用户感知就是"打开工具界面后内存一直很高"。

**Files:** `App.Workspace.cs:79-104`（唯一调用点，工作台隐藏 60 秒）、`ViewModels/WorkspaceViewModel.cs:66-70`、`Services/ImageCache.cs:237-252`

**Fix:** 在 `CaptureWindow.OnClosed`、`ScrollResultWindow.OnClosed`、`ClipboardImageZoomWindow.OnClosed` 里调用 `ImageCache.Instance.Clear()`；或给 `Clear()` 增加引用计数/延迟释放策略，在确认无 `Image` 控件持有后主动 `Dispose()`。

**Caveat:** 不要直接改成 `Dispose()`——注释里已说明被淘汰的位图可能仍被 `Image.Source` 引用，主动释放会渲染崩溃（`ImageCache.cs:22-23`）。

---

### 🟡 F7. WGC 帧缓冲在录屏失败路径常驻（1 块 LOH）

**Impact:** `WgcFrameSource.cs:64` 分配 `new byte[width * 4 * height]`，4K 全屏约 33 MB 单块 LOH；对象整体被 `RecordingSession._source`（readonly）持有，只有 `Dispose()` 被调用才释放。F4 的异常路径会让这整块常驻，并把 LOH 高水位顶上去。

**Files:** `Services/Recording/WgcFrameSource.cs:38,64,460-470`, `Services/Recording/RecordingSession.cs:497-527`

**Fix:** 随 F4 一起修（保证 `Dispose()` 必达）；另可在 `WgcFrameSource.Dispose()` 里把 `_pixels` 改为可空字段并置 null，让 LOH 块更早进入回收。

---

## 三、已核实「不是问题」（避免误改）

- ✅ `CaptureWindow.OnClosed` 已释放 `_frozenBitmap`、`_frozenPixels`、`_annotationBitmap`、`_uiProbe`/`_refinementProbe`/`_msaaProbe`、`_ocrCancellation`，并 `RemoveHitTestHook()` + `TrimFreezeMemory()` 压缩 LOH（`axaml.cs:1001-1068`）——**清理清单本身写得很好，缺的只是两个 Timer**
- ✅ `PinnedImageWindow`、`ClipboardImageZoomWindow`、`ScrollResultWindow` 均有 `OnClosed` + `Dispose()`；`ScrollResultWindow` 预览按 16 MP 预算降采样解码
- ✅ `WgcFrameSource` 复用同一个 `_pixels`，每帧不分配（`TryGetFrame` 返回同一数组），`SoftwareBitmap`/`IMemoryBufferByteAccess` 都 `using` 释放
- ✅ `RecordingRegionOverlay.cs:60` 的 800 ms `DispatcherTimer` 是局部变量且 Tick 内自停，**不是泄漏**
- ✅ `RecordingBarWindow.Closed` 会 `-=` 解绑 `StateChanged`；`CaptureWindow.Recording.cs:162,198` 停止/取消路径也解绑
- ✅ `RecordingSession.Dispose/Abandon` 覆盖 encoder/audio/wav/watermark/source/cts；`CursorHighlighter` 只持有托管 `List`，无需 Dispose
- ✅ `App.Pipelines.cs:267` 贴图窗口 `Closed += _pins.Remove(pin)`；`ToastService` 在 `Closed` 里 `_current = null`
- ✅ `AiClient` 的 `new HttpClient(...)` 是单实例长生命周期字段，不构成 socket exhaustion
- ✅ 宽口径 `-=` 命中 12 处中，4 处 `.Result`/`window.Result` 全是 `await window.Result` 自定义属性或 `capture.Result` 属性，**sync-over-async 实际 0 处**
- ✅ 事件订阅 68 处 vs 显式解绑 5 处，看似 13:1 失衡，但绝大多数是窗口内控件订阅自身处理器（同生命周期，关窗整体回收）；真正"长生命周期源 → 短生命周期订阅者"只有 `_session.StateChanged`、`_store.Changed`、`settingsViewModel.*`、`Dispatcher.UIThread.UnhandledException` 等少数，且大部分已处理

---

## 四、静态反模式扫描（skill 标准配方）

### 扫描执行清单（精确命中数，非估算）

| 配方 | 命中 | 配方 | 命中 |
|---|---:|---|---:|
| C1 `.IndexOf("str")` 无 StringComparison | 0 | C2 `.Substring(` | 2 |
| C3 `StartsWith/EndsWith` 无比较参数 | 0 | C4 `.Contains("str")` | 8 |
| M1 `.ToLower()/.ToUpper()` | 0 | M2 链式 `.Replace(` 3+ | 1 |
| M3 `params` 数组分配 | 2 | M4 `.All/.Any(char.` | 1 |
| A1 `async void`（排除 event 行） | 30 | S1 未 sealed 类 / S2 已 sealed | 51 / 72 |
| L1 LINQ `Select/Where/OrderBy/GroupBy` | 93 | L2 `.All/.Any` | 19 |
| L3 `new Dictionary/List<` | 92 | L4 `static readonly Dictionary<` | 6 |
| R1 `RegexOptions.Compiled` | 0 | R2 `new Regex(` | 0 |
| R3 `[GeneratedRegex]`（正面） | 6 | REG3 `Regex.Match/IsMatch/Replace` | 1 |
| I1 `JsonSerializer` | 20 | I2 `new HttpClient(` | 1 |
| I3 `FileStream/MemoryStream` 创建 | 18 | IO1 `new JsonSerializerOptions` 未缓存 | 0 |
| COL1 static Dictionary（Frozen 候选） | 6 | COL2 `FrozenDictionary`（正面） | 0 |
| COL3 方法内 `new List<` | 76 | COL4 方法内 `new Dictionary<` | 16 |
| COL5 `StringComparer.CurrentCulture` | 0 | COL6 LINQ 链 | 87 |
| X1 事件订阅 `+=`（宽口径） | 135 | X2 `-=`（宽口径） | 12 |
| X3 `IDisposable` 声明 | 19 | X4 `Dispose(` | 86 |
| X5 Bitmap 引用 | 49 | X6 `new byte[` | 19 |
| X7 static 集合字段 | 74 | X8 Timer | 13 |
| X9 `CancellationTokenSource` | 16 | X10 `Process/ProcessStartInfo` | 13 |
| X11 `Closed/Closing +=` | 9 | X12 `WeakEvent/WeakReference` | 4 |
| X13 `static event` | 0 | X14 `.Result/.Wait()` | 4（已核实 0 处为真） |
| X15 `ObservableCollection/.Add(` | 330 | — | — |

### 🟡 Moderate（规则类，与内存关系较弱）

#### S-1. 51 个未 sealed 的叶子类（51 instances，其中 72 个已 sealed）
**Impact:** 按 skill 规模规则 50+ 触发 🟡 elevated priority；影响 JIT 虚调用内联与类型检查，非内存问题。
**Fix:** 对确认无子类的叶子类补 `sealed`；`CaptureWindow` 等被 XAML 继承或分部扩展的类需排除。
**Caveat:** 51/123 未 sealed，但其中相当比例是有意可扩展的窗口/VM 基类，需人工甄别，不建议批量替换。

#### S-2. 6 个 `static readonly Dictionary<` 可换 `FrozenDictionary`（6 instances）
**Impact:** 只读查表场景约 50% 查找提速；与内存无关。
**Fix:** 确认构造后不再变更的字典改 `.ToFrozenDictionary()`。

#### S-3. `async void` 事件处理器 30 处（30 instances）
**Impact:** 多数是 Avalonia UI 事件处理器（合理用法），但异常无法被调用方捕获会直接崩进程；其中 `App.Capture.cs:30` 的 `CaptureAndRecognize` 与 F3 的悬挂状态机直接相关。
**Fix:** 至少给 `CaptureAndRecognize` 加全量 `try/catch/finally`；其余保持现状即可。

#### S-4. 链式 `.Replace` 1 处、`params` 2 处、`.Substring` 2 处
**Impact:** 非热路径上的少量分配，对截屏/录屏内存占用无可测量影响。
**Fix:** 仅在后续 benchmark 证明该路径为热点时处理。

### ℹ️ Info

- `JsonSerializer` 20 处、`Stream` 18 处、`LINQ` 93 处：均不在截屏/录屏的逐帧热路径上（`RecordingSession.Run()` 里没有 LINQ、没有 JSON、没有 `new byte[]`），按 skill 规则**不建议**做微优化。
- `RegexOptions.Compiled` 0 / `new Regex(` 0 / `[GeneratedRegex]` 6：正则实践良好，0 处反模式。
- `new JsonSerializerOptions` 未缓存 0 / `StringComparer.CurrentCulture` 0：良好。

### 正面发现

- ✅ `[GeneratedRegex]` 6 处，0 个 `new Regex(` / `RegexOptions.Compiled`
- ✅ 0 处 sync-over-async、0 处未缓存 `JsonSerializerOptions`、0 处 `StringComparer.CurrentCulture`
- ✅ `ImageCache` 按内容哈希 + 按字节 LRU + 解码阶段降采样（避免 4K 原图进内存）
- ✅ `WgcFrameSource` 帧缓冲复用，不做逐帧分配
- ✅ `RecordingSession.Run()` 的取帧循环里没有 LINQ / JSON / 临时大数组分配

---

## 五、复测与验证方法（静态审查不能替代实测）

项目已经埋好了内存日志点，直接可用：

- `App.Capture.cs:244`「截图窗口已显示」、`:247`「截图窗口已关闭」
- `App.Workspace.cs:69`「工作台打开」、`:103`「工作台空闲释放」
- `MemoryText()` 输出 `WorkingSet` 与 `GC.GetTotalMemory(false)`（`App.Workspace.cs:107-109`）

**建议的复测脚本**（不改代码，只看日志）：

1. 连续 10 次「打开截图 → 直接 Esc 关闭」，观察 `WorkingSet` 是否单调上涨 → 验证 F1
2. 连续 10 次「开始区域录制 → 立刻取消 / 让 ffmpeg 下载对话框取消」，再按录屏热键 → 验证 F2（预期会提示"已经在录制中"）
3. 连续 10 次「打开水印编辑器 → 换图 → 关闭」→ 验证 F5
4. 录屏 30 秒后停止，记录停止瞬间 UI 是否卡顿 → 验证 F4

**动态取证**（用刚装的技能）：

- `dotnet-trace-collect` / `dotnet-counters`：盯 `GC Heap Size`、`LOH Size`、`Gen2 Collections`
- `dump-collect` 抓 dump 后用 `dotnet-dump` / WinDbg 看 `CaptureWindow` 的 GC Root 路径；F1 的预期根路径就是 `Dispatcher → DispatcherTimer → Tick delegate → CaptureWindow`
- `microbenchmarking` 仅用于验证修复前后的分配差异，定位泄漏不要用基准测试

---

## 六、修复优先级

1. **F1**（改 1 个方法，收益最大，风险最低）——`CaptureWindow.OnClosed` 补齐两个 Timer 的 Stop/解绑/置 null
2. **F2 + F3**（同一处 `App.Capture.cs`，一起改）——赋值时机 + `try/finally` 清理 + `WaitForClosedAsync` 解绑
3. **F4**——把 `session.Dispose()` 移出 UI 线程，统一收尾路径
4. **F5**——水印编辑器补 `OnClosed`
5. **F6 / F7**——扩大缓存清理触发面、保证 WGC 缓冲必达释放

F1/F2 修完后，建议先跑上面的复测脚本确认 `WorkingSet` 回落，再动 F4（涉及并发收尾，改动风险相对高）。按项目习惯，改完走 `dotnet build` + `dotnet test`（`run-tests` 技能），再用 `verification-before-completion` 收尾。

---

## 汇总表

| Severity | Count | Top Issue |
|----------|-------|-----------|
| 🔴 Critical | 4 | F1 `OnClosed` 未停 DispatcherTimer，窗口与 66 MB 冻结帧永久驻留 |
| 🟡 Moderate | 7 | F5 水印编辑器位图不释放；F6 缓存清理触发面过窄 |
| ℹ️ Info | 4 组 | 规则类分配与未 sealed 类，非热路径，不建议动 |

> ⚠️ **Disclaimer:** These results are generated by an AI assistant and are non-deterministic. Findings may include false positives, miss real issues, or suggest changes that are incorrect for your specific context. Always verify recommendations with benchmarks and human review before applying changes to production code. 本报告为静态代码审查，未经运行时 profiling 验证；F1–F7 的"每次代价"为按 4K 分辨率估算，实际数值以第五节复测为准。

---

## 七、修复执行记录（2026-11-26，已改代码）

按第六节优先级 F1 → F2+F3 → F4 → F5 → F6/F7 全部落地，共改动 **7 个源文件**。验证：`dotnet build Sparrow.slnx` → **0 警告 / 0 错误**；`dotnet test tests/Sparrow.Tests/Sparrow.Tests.csproj --no-build` → **859 通过 / 0 失败 / 0 跳过**。改动未提交。

| 编号 | 文件 | 改动 |
|---|---|---|
| F1 | `Views/CaptureWindow.axaml.cs` | `OnClosed` 补齐三个 Timer：`_groupShowTimer` / `_groupCloseTimer` 的 `Stop()` + `Tick -=` + 置 null，`_recordTimer` 的 `Stop()` + 置 null（Tick 是内联 lambda，Stop 后 timer 与 lambda 成为可回收孤岛） |
| F2 | `App.Capture.cs` | `RecordingSessionStarter` 改 async lambda：会话创建返回 null 或抛异常时撤销 `_activeCaptureWindow` 占位（`ReferenceEquals` 保护，避免误清别的窗口）；保留「先占位」以挡住倒计时/下载期间的重复启动 |
| F3 | `App.Pipelines.cs`、`App.Capture.cs` | `WaitForClosedAsync` 改命名 handler + `finally` 解绑、`RunContinuationsAsynchronously`、30 分钟兜底超时；`CaptureAndRecognize` 在 `await` 外包 `try/finally`，保证 `prewarmedProbe` 释放与本窗口占位撤销 |
| F4 | `App.Record.cs` | `session.Dispose()` 从 `Finished`（UI 线程）移到控制条 `Closed` 回调里的 `Task.Run` 后台执行；`_recordingBar` 也在 `Closed` 里用 `ReferenceEquals` 保护后清空。覆盖停止 / 取消 / Completed / Faulted / StopAsync 异常 / 用户直接关控制条全部路径 |
| F5 | `Overlays/WatermarkEditorWindow.axaml.cs` | 新增 `SetWatermarkImage()`（先断 `Image.Source` 再 Dispose 旧位图）与 `OnClosed`；`ApplyLayout` / 选图 / 清图 / 双击复位统一走它 |
| F6 | `Views/CaptureWindow.axaml.cs`、`Views/ScrollResultWindow.axaml.cs` | 两个工具窗口 `OnClosed` 调 `ImageCache.Instance.Clear()`；**未**改 `Clear()` 的「不主动 Dispose」语义 |
| F7 | `Services/Recording/RecordingSession.cs` | `Dispose()` / `Abandon()` 的资源释放改为逐项 `DisposeQuietly(Action)`，任一单项抛异常也不跳过 `_source.Dispose()`（WGC 帧池 + 33 MB LOH 帧缓冲） |

### 与原建议的三处有意偏差

1. **F6 只挂 2 个窗口，没挂 `ClipboardImageZoomWindow`**：它属于剪贴板历史模块，关灯箱清缓存会让用户回到工作台时重新解码全部缩略图，是负收益；该模块已有「隐藏 60 秒释放」覆盖。
2. **F7 没有把 `_pixels` 置 null**：`Dispose()` 的 `_worker.Wait(3s)` 超时被吞掉后 worker 可能仍在读 `_pixels`，置 null 会引入 NRE（虽被 `Run()` 的 catch 兜住，但会打断录制）；改为从根上保证 `_source.Dispose()` 必达，收益相同且零风险。
3. **F3 的 30 分钟超时是防御兜底**：正常关窗走 `Closed` 立即返回；只有窗口被 `Hide` / 异常销毁时才超时，让 `async void` 状态机结束（按「取消」处理），不会中断正常使用。

### 仍需真机复测（单元测试覆盖不到 UI 生命周期）

按第五节脚本：① 连续 10 次「打开截图 → Esc 关闭」看 `WorkingSet` 是否单调上涨（F1）；② 连续 10 次「区域录制 → 取消」后按录屏热键（F2）；③ 水印编辑器反复换图 / 开关（F5）；④ 录屏 30 秒停止时 UI 是否卡顿（F4）。要定位 GC Root 用 `dotnet-trace-collect` / `dump-collect`，F1 预期根路径 `Dispatcher → DispatcherTimer → Tick → CaptureWindow`。
