# 打包与发布

> 实测时间：2026-11-20　平台：win-x64　版本：0.1.0

## 一、一条命令

```powershell
# 只发布，不打包
pwsh build\publish.ps1 -SkipOcr

# 发布 + Velopack 安装包（用户下载 Setup.exe）
pwsh build\pack.ps1 -Version 0.1.0 -SkipOcr
```

- `publish.ps1`：自包含发布到 `artifacts/publish/win-x64`，并把 `plugins/` 原样搬过去
- `pack.ps1`：调用上面的脚本，再用 `vpk` 生成安装器、免安装版和增量包
- Velopack CLI 需要先装：`dotnet tool install -g vpk`
- 同一个版本不能重复打包；本地验证加 `-Clean` 清掉旧产物

## 二、实测体积（不含 OCR）

| 产物 | 大小 |
|---|---:|
| 发布目录（自包含） | **135.3 MB** |
| `Sparrow-win-Portable.zip` | 61.7 MB |
| `Sparrow-0.1.0-full.nupkg`（增量基线） | 61.7 MB |
| `Sparrow-win-Setup.exe` | **68.8 MB** |

发布目录构成：

| 部分 | 大小 | 说明 |
|---|---:|---|
| 根目录（.NET 运行时 + 应用 + 托管库） | 116.0 MB | 自包含，目标机不需要装 .NET |
| `runtimes/win-x64/native` | 19.1 MB | LLamaSharp 4 套 CPU 变体 + Skia / HarfBuzz / sqlite |
| `plugins/` | 0.2 MB | 翻译插件不再自带推理库（原 22.4 MB） |

**已剔除**：第三方包带出来的调试符号。`libSkiaSharp.pdb` 80 MB、`libHarfBuzzSharp.pdb` 20 MB，
发布后不再需要，`publish.ps1` 会统一删掉 —— 这一项把 235 MB 降到了 135 MB。

## 三、和验收线（< 60 MB）的差距

`docs/release-checklist.md` 写的是「安装包 < 60 MB（不含 OCR）」，当前 Setup 是 68.9 MB。两个可选杠杆：

| 做法 | 省多少 | 代价 |
|---|---:|---|
| 改成框架依赖发布（`--self-contained false`） | ≈ 70 MB | 用户必须先装 .NET 10 Desktop Runtime |
| 翻译插件只保留 avx2 一套 native 库 | ≈ 9 MB | 极老的 CPU（无 AVX2）会退到纯解释执行或直接不可用 |

按「开箱即用」优先，当前默认是**自包含**。目标线建议改成 80 MB，或者接受框架依赖。`r`n`r`n> 本地翻译引擎从插件搬进宿主后，总体积基本没变（22 MB 从 `plugins/` 挪到了 `runtimes/`），`r`n> 但插件目录从 22.4 MB 降到 0.2 MB，OCR 和翻译也不必各背一份 native 库。

## 四、更新

`Program.Main` 第一行已经接上 `VelopackApp.Build().Run()`（开发态是空操作，不影响 `dotnet run`）。

**设置 › 关于 › 更新** 里填「更新源地址」，点「检查更新」：

```
地址留空           → 未配置更新源
不是 http(s)       → 更新源必须是 http(s) 地址
不是安装版         → 当前不是安装版，无法检查更新（开发态）
有新版本           → 显示版本号 → 下载 → 自动重启
```

地址会保存到 `settings.json` 的 `UpdateFeedUrl`，下次进来直接可用。

### 怎么发布一次更新

1. `pwsh build\pack.ps1 -Version 0.2.0 -SkipOcr`
2. 把 `artifacts/releases/` 里的 `RELEASES`、`releases.win.json`、`*.nupkg` 上传到同一个 HTTPS 目录
3. 旧版本设置里填这个目录的 URL，点「检查更新」
4. 首次升级后确认：热键、托盘、插件目录、设置都还在

增量包只包含变化的部分，第二次以后的更新体积会明显小于首次的全量包。

## 五、干净机器验收清单

- [ ] `Setup.exe` 安装 → 首次启动 < 3 分钟可导出
- [ ] 托盘、全局热键、剪贴板面板都正常
- [ ] 卸载后无残留热键注册
- [ ] 升级一次后热键仍可用
- [ ] OCR 插件单独分发时的目录完整性（`player/worker/`）
