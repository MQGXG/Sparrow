# 第三方组件声明

## FFmpeg（屏幕录制编码器）

| 项 | 内容 |
|---|---|
| 位置 | `tools/ffmpeg/`（**不随仓库分发**；构建时复制到输出目录 `tools/ffmpeg/`） |
| 来源 | FormatFactory 附带的 FFmpeg 构建：`ffmpeg (Format Factory Revision) version N-110064-gcbcc817353` |
| 许可 | GPL v2+（构建包含 libx264，同样是 GPL） |
| 用途 | 屏幕录制的 H.264 / MP4 编码；视频后处理插件（裁剪 / 压缩 / GIF / 抽帧） |
| 交互方式 | 独立进程，只通过 stdin 接收原始 BGRA 帧，不链接进 `Sparrow.App` |

### 分发合规

包含 FFmpeg 的安装包在分发时必须同时满足 GPL：

1. 提供 FFmpeg 源码或明确的获取方式（<https://ffmpeg.org/download.html>）；
2. 保留本声明与 FFmpeg 的许可证文本；
3. 说明对 FFmpeg 的修改（本仓库未修改 FFmpeg 源码）。

Sparrow 主体通过进程边界与 FFmpeg 通信，不把 FFmpeg 链接进主程序；
这不改变 FFmpeg 自身的 GPL 义务，但主程序不需要因此开源。

> 注意：当前开发机使用的是第三方（FormatFactory）构建，版本号 `N-110064-gcbcc817353`
> 对应上游提交 <https://github.com/FFmpeg/FFmpeg/commit/cbcc817353>。
> 公开发布前建议确认该构建的源码获取方式，或替换为官方 / BtbN 的 GPL 构建
> （<https://github.com/BtbN/FFmpeg-Builds>，`scripts/fetch-ffmpeg.ps1` 默认拉这个）。

### 获取方式（不进 Git）

仓库不提交 ffmpeg 二进制（约 45 MB）。三种方式任选：

1. **开发机 / 打包机**：运行一次

   ```powershell
   pwsh -File scripts/fetch-ffmpeg.ps1
   ```

   会下载 BtbN 的 win64 GPL 构建并解压到 `tools/ffmpeg/`。

2. **首次录制自动下载**：直接启动应用点「开始录制」，缺失时会弹出下载窗口，
   解压到 `%LOCALAPPDATA%\Sparrow\ffmpeg`（宿主和四个视频插件共用）。

3. **手动放置**：把下面这些文件放到 `tools/ffmpeg/`：

   ```text
   tools/ffmpeg/
   ├─ ffmpeg.exe
   ├─ avcodec.dll
   ├─ avdevice.dll
   ├─ avfilter.dll
   ├─ avformat.dll
   ├─ avutil.dll
   ├─ swresample.dll
   ├─ swscale.dll
   ├─ postproc.dll
   ├─ libgcc_s_seh-1.dll
   └─ libwinpthread-1.dll
   ```

查找顺序：`tools/ffmpeg/` → `%LOCALAPPDATA%\Sparrow\ffmpeg` → `PATH`。

### 发布安装包

Velopack 打包前先在打包机执行 `scripts/fetch-ffmpeg.ps1`（或手动放置），
构建会把 `tools/ffmpeg/` 复制进输出目录，安装包随之携带 FFmpeg，
终端用户不需要额外下载。

如果既没有二进制、下载也失败，录制入口会提示「未找到 ffmpeg.exe / FFmpeg 下载失败」，
不会崩溃。
