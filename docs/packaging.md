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
| 发布目录（自包含） | **135.5 MB** |
| `Sparrow-win-Portable.zip` | 61.7 MB |
| `Sparrow-0.1.0-full.nupkg`（增量基线） | 61.7 MB |
| `Sparrow-win-Setup.exe` | **68.9 MB** |

发布目录构成：

| 部分 | 大小 | 说明 |
|---|---:|---|
| .NET 运行时 + 应用 + Skia | 112.6 MB | 自包含，目标机不需要装 .NET |
| `plugins/` | 22.5 MB | 其中翻译插件 22.4 MB（LLamaSharp + 4 套 CPU 变体） |

**已剔除**：第三方包带出来的调试符号。`libSkiaSharp.pdb` 80 MB、`libHarfBuzzSharp.pdb` 20 MB，
发布后不再需要，`publish.ps1` 会统一删掉 —— 这一项把 235 MB 降到了 135 MB。

## 三、和验收线（< 60 MB）的差距

`docs/release-checklist.md` 写的是「安装包 < 60 MB（不含 OCR）」，当前 Setup 是 68.9 MB。两个可选杠杆：

| 做法 | 省多少 | 代价 |
|---|---:|---|
| 改成框架依赖发布（`--self-contained false`） | ≈ 70 MB | 用户必须先装 .NET 10 Desktop Runtime |
| 翻译插件只保留 avx2 一套 native 库 | ≈ 9 MB | 极老的 CPU（无 AVX2）会退到纯解释执行或直接不可用 |

按「开箱即用」优先，当前默认是**自包含**。目标线建议改成 80 MB，或者接受框架依赖。

## 四、更新

`Program.Main` 第一行已经接上 `VelopackApp.Build().Run()`（开发态是空操作，不影响 `dotnet run`）。
剩余的是**更新源**：

1. 把 `artifacts/releases/` 里的 `RELEASES`、`*.nupkg`、`Setup.exe` 传到 HTTPS 静态目录（自托管也行）
2. 在宿主里用 `UpdateManager` 指向该地址，做一个「检查更新」入口
3. 首次安装后升级一次，确认热键、托盘、插件目录都在

## 五、干净机器验收清单

- [ ] `Setup.exe` 安装 → 首次启动 < 3 分钟可导出
- [ ] 托盘、全局热键、剪贴板面板都正常
- [ ] 卸载后无残留热键注册
- [ ] 升级一次后热键仍可用
- [ ] OCR 插件单独分发时的目录完整性（`player/worker/`）
