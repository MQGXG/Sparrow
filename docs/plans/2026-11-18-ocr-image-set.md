# 图片集输入 + OCR 单工具合并 Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 合并两张 OCR 卡为一张，新增「图片集」输入形态，工具页用缩略图条展示选中的图片。

**Architecture:** 宿主采集 1..N 张图片（剪贴板 / 拖入 / 文件选择），以 `ImageSetInput` 交给插件；OCR 插件只保留 `sparrow.ocr.image`，一次 worker 会话连续识别。SDK 只增不改（`Images = 6`、`ImageInput.Name`、`ImageSetInput`）。

**Tech Stack:** .NET 10、Avalonia 11、xUnit v3 + Shouldly、PaddleOCR worker。

---

### Task 1: SDK 契约（1.5）

**Files:**
- Modify: `src/Sparrow.Plugin.Sdk/Enums.cs`
- Modify: `src/Sparrow.Plugin.Sdk/Inputs.cs`
- Modify: `src/Sparrow.Plugin.Sdk/SdkInfo.cs`
- Test: `tests/Sparrow.Tests/PaletteViewModelTests.cs`（后续任务覆盖）

**Step 1:** `InputShape` 末尾加 `Images`（保持已有数值不变），`ImageInput` 加 `Name`，新增 `ImageSetInput`。
**Step 2:** `SdkInfo.Version = "1.5"`。
**Step 3:** `dotnet build Sparrow.slnx`，预期 0 错误。

### Task 2: 宿主图片集输入

**Files:**
- Modify: `src/Sparrow.App/ViewModels/WorkspaceViewModel.cs`
- Create: `src/Sparrow.App/ViewModels/InputImageViewModel.cs`

**Step 1:** 状态与属性：`_inputImages`、`InputImages`、`IsImagesInput`、`HasInputImages`、`CanClearInputImages`、`InputHint`（张数 + 前 3 个文件名）。
**Step 2:** 方法：`AddInputImages(paths)`（校验扩展名/大小/数量/总量）、`AddClipboardImage()`、`RemoveInputImage(item)`、`ClearInputImages()`、`SetPreviewImage(item)`。
**Step 3:** `BuildInput` 增加 `InputShape.Images` 分支；无图时返回「还没有图片…」。
**Step 4:** `AcceptDroppedFiles` 分流：全是图片 → 追加并切到 Images 工具；含非图片 → 保持 Files 逻辑。
**Step 5:** 执行后 `Source = ToolSource.Dropped`（图片集）并保留进度代次。
**Step 6:** `dotnet build`。

### Task 3: 工作区视图

**Files:**
- Modify: `src/Sparrow.App/Views/WorkspaceWindow.axaml`
- Modify: `src/Sparrow.App/Views/WorkspaceWindow.axaml.cs`

**Step 1:** 非文本输入区加缩略图条（`ItemsControl` + `WrapPanel`，88px 缩略图、文件名、移除按钮）。
**Step 2:** 按钮：`添加图片…`（`OpenFilePickerAsync`，图片过滤器、多选）、`粘贴剪贴板`、`清除`。
**Step 3:** 页内大图浮层（点缩略图打开，Esc / 点空白关闭）。
**Step 4:** headless 测试断言缩略图与按钮可见（Task 6）。

### Task 4: 面板与截图链路

**Files:**
- Modify: `src/Sparrow.App/ViewModels/PaletteViewModel.cs`
- Modify: `src/Sparrow.App/App.axaml.cs`
- Modify: `src/Sparrow.App/Services/ToolRegistry.cs`

**Step 1:** 面板 `InputShape.Images` → 剪贴板图片包成 1 张 `ImageSetInput`，名字「剪贴板图片」。
**Step 2:** `ToolRegistry.FindAccepting` 不变；截图改查 `InputShape.Images`，包成 1 张，名字「截图」。
**Step 3:** 截图 OCR 仍要求 `OcrResultPayload`，行框逻辑不动。

### Task 5: 插件合并

**Files:**
- Modify: `plugins/sparrow.plugin.ocr/OcrImageTool.cs`
- Delete: `plugins/sparrow.plugin.ocr/OcrBatchTool.cs`
- Modify: `plugins/sparrow.plugin.ocr/plugin.json`
- Modify: `plugins/sparrow.plugin.ocr/Sparrow.Plugin.Ocr.csproj`

**Step 1:** `Accepts = Images`，名称/描述改为「剪贴板、拖入或选择的多张图片」。
**Step 2:** 循环识别，`Progress` 报「正在识别 2/5：a.png」；单张失败不中断。
**Step 3:** 1 张输出纯文本；多张输出 `## 文件名` 分节 + 末尾未识别列表（移植 `ComposeBatchText`）。
**Step 4:** 清单只留 `sparrow.ocr.image`，版本 0.3.0；删除批量工具文件。
**Step 5:** `dotnet build`。

### Task 6: 测试

**Files:**
- Modify: `tests/Sparrow.Tests/OcrImageToolTests.cs`
- Delete: `tests/Sparrow.Tests/OcrBatchToolTests.cs`
- Modify: `tests/Sparrow.Tests/OcrBatchIntegrationTests.cs`
- Modify: `tests/Sparrow.Tests/WorkspaceViewModelTests.cs`
- Modify: `tests/Sparrow.Tests/WorkspaceWindowTests.cs`
- Modify: `tests/Sparrow.Tests/PaletteViewModelTests.cs`

**Step 1:** 插件：单张无标题、多张分节、失败不中断、空图片集、非 Images 输入。
**Step 2:** 真机集成：`ImageSetInput` 两张，一次会话。
**Step 3:** 宿主：拖入两张图片自动进 OCR；清除；无图执行给提示。
**Step 4:** 面板：剪贴板图片 → Images 上下文；无图报错。
**Step 5:** 视图：图片工具页缩略图条与「添加图片…」可见。
**Step 6:** `dotnet test`，预期全绿。

### Task 7: 文档

**Files:**
- Modify: `docs/plugin-model.md`（SDK 1.5、Inputs 列表、输入形态）
- Modify: `docs/tool-catalog.md`（删批量行、更新图片识别行）
- Modify: `docs/ui-spec.md`（图片集输入与缩略图）
- Modify: `README.md`（工具数、测试数、插件说明）

**Step 1:** 四处同步；**Step 2:** `dotnet test` 复跑；**Step 3:** 提交。
