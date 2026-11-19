# Sparrow 品牌图标

主图：`sparrow-icon-1024.png`（1024×1024 PNG，25% 圆角，圆角外透明）。

| 文件 | 尺寸 | 用途 |
|---|---|---|
| `src/Sparrow.App/Assets/sparrow.ico` | 16 / 20 / 24 / 32 / 40 / 48 / 64 / 128 / 256 | exe、资源管理器、任务栏 |
| `src/Sparrow.App/Assets/sparrow-32.png` | 32 | 托盘、设置页（30px）、标题栏（22px） |
| `src/Sparrow.App/Assets/sparrow-64.png` | 64 | 窗口图标、工作区空态（56px） |

小尺寸从 1024 主图缩放，不要直接把主图缩到 16px 交付：

- 16–32px 用 LANCZOS 缩放后做一次轻微锐化，并补 5% 对比度；
- 48–64px 只做轻锐化；
- 圆角固定 25%，与主图一致，四角必须透明而不是黑色。

`sparrow.ico` 里 16–128 存 BMP 条目、256 存 PNG 条目；`Icon=` 统一引用
`sparrow-64.png`，让 150% / 200% DPI 的标题栏也有足够像素。
