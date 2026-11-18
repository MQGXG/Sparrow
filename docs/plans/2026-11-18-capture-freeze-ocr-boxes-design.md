# 2026-11-18 截图 OCR：先冻结再识别 + 识别框开关

> 状态：已实现

## 一、问题

1. 页面滚动后立刻截图，OCR 识别的是滚动前的帧。
   原流程在点动作时才抓图：遮罩 `Opacity=0` → 等 80ms → BitBlt；
   滚动惯性/重绘延迟会让 BitBlt 拿到旧帧。
2. 识别框始终显示，满屏行框干扰阅读，没有开关。

## 二、冻结帧

流程改为：

```text
按 Ctrl+Alt+C
→ 遮罩 Opacity=0，等 120ms（确保遮罩已透明）
→ 抓「连续两帧一致」的整屏原始 BGRA
→ 显示冻结图作为遮罩背景
→ 框选
→ OCR / 复制图 / 保存 / 固定：从冻结图裁剪，不再抓屏
```

- 稳定判定：每 50ms 抓一帧，连续两帧 `SequenceEqual` 即返回；最多 8 轮，超时用最后一帧
- 不调用 `DwmFlush`：远程桌面/无合成环境会阻塞，导致窗口永远停在透明状态
- 滚动截图（`G`）例外：关闭遮罩后按选区矩形对实时屏幕抓帧
- 冻结失败自动退回实时抓屏（同样等稳定帧）

代价：

- 热键到遮罩出现多 100–300ms
- 整屏 BGRA 占用（4K 约 33MB），关闭窗口释放
- 视频等持续动画等不到稳定帧，超时用最后一帧

## 三、识别框开关

三档，工具栏「框」按钮或 `B` 循环：

| 模式 | 行为 |
|---|---|
| 悬停（默认） | 不画框；鼠标靠近哪行只亮哪行，点它复制 |
| 全部 | 所有行框常亮 |
| 关闭 | 不画框、不参与点行复制 |

- 悬停命中：行框外扩 4 DIP，避免细线难点
- 关闭时 `OcrLayer.IsHitTestVisible=false`，点击回到「清除 overlay」逻辑
- 选择写入 `settings.json` 的 `CaptureOcrBoxMode`（`all` / `hover` / `off`），下次沿用

## 四、实现

- `ScreenCapture.CaptureStableRaw` / `CaptureStable` / `Crop` / 公开 `EncodePng`
- `CaptureWindow`：冻结帧图层、`FreezeScreenAsync`、从冻结图裁剪；`OcrBoxMode` 与悬停命中
- `AppSettings.CaptureOcrBoxMode` + `SettingsViewModel.SetCaptureOcrBoxMode`
- 测试：模式解析/循环、冻结帧裁剪坐标、改模式后行框可见性

## 五、不做

- 多屏不同缩放时的冻结帧换算（沿用主屏缩放，M1 已知限制）
- 视频/动画场景的“最新帧”保证
