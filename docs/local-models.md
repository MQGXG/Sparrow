# 本地模型（宿主基础设施）

> 需要本地推理的工具不应该各自造下载器和缓存。宿主提供「模型在哪」，插件只负责推理。
> 这套能力的第一个使用者是翻译插件（离线引擎），但它不是翻译专用设施。

## 一、为什么放在宿主

| 理由 | 说明 |
|---|---|
| 下载要写用户目录 | 插件没有文件写权限（`IToolHost` 只给只读的 `PluginDirectory`） |
| 下载要显示进度 | 插件不提供 UI；进度只能由设置页渲染 |
| 缓存必须唯一 | 同一个模型可能被多个插件用到，各下各的会重复占盘 |
| 许可能约束 | 下载前要展示大小、内存要求和许可限制，这是宿主界面的事 |

插件能拿到的只有两样东西：**清单**（`LocalModels.All`）和**文件路径**（`EnsureAsync`）。

## 二、SDK 契约

```csharp
public interface ILocalModels
{
    IReadOnlyList<LocalModelInfo> All { get; }
    bool IsInstalled(string modelId);
    Task<string?> EnsureAsync(string modelId, IProgress<double>? progress = null, CancellationToken ct = default);
    Task<bool> DeleteAsync(string modelId, CancellationToken ct = default);
}
```

`LocalModelInfo`：`Id / Name / Description / SizeBytes / MemoryGb / License / DownloadUrls`。

宿主未注入时插件拿到 `NullLocalModels.Instance`（`All` 为空、`IsInstalled` 恒假、`EnsureAsync` 返回 null）。插件必须处理这三种情况并给出可读提示。

## 三、下载源与自托管

`DownloadUrls` 是**有序回退列表**，按顺序尝试，第一个成功即止。

```csharp
new LocalModelInfo(
    "hy-mt-1.8b",
    "混元翻译 1.5 · 1.8B",
    ...,
    [
        "https://modelscope.cn/models/Tencent-Hunyuan/HY-MT1.5-1.8B-GGUF/resolve/master/HY-MT1.5-1.8B-Q4_K_M.gguf",
        "https://huggingface.co/tencent/HY-MT1.5-1.8B-GGUF/resolve/main/HY-MT1.5-1.8B-Q4_K_M.gguf"
    ]);
```

- **ModelScope 在前**：国内直连可用；实测 HuggingFace 在部分网络下直接超时
- **HuggingFace 兜底**：海外用户和官方仓库
- **自托管**：以后只要往数组最前面插一条自己的镜像地址，下载、缓存、删除、设置页都不用改

不支持断点续传（ModelScope 的 CDN 对 Range 请求返回 403），失败后整文件重下。

## 四、存储与校验

```
%LOCALAPPDATA%\Sparrow\models\
└── hy-mt-1.8b\
    └── model.gguf
```

- 下载先写 `model.gguf.part`，成功后原子改名，避免半个文件被当成「已安装」
- 校验只做「文件非空」：清单里的大小是精确值，但量化版本会变，卡死大小反而误杀
- SHA256 等下载源稳定后再加（清单里已经有 `DownloadUrls` 的位置放它）

## 五、翻译插件怎么用

| 项 | 做法 |
|---|---|
| 推理库 | `LLamaSharp` + `LLamaSharp.Backend.Cpu`（**0.30+**，0.25 不认 `hunyuan-dense` 架构） |
| 依赖分发 | 插件目录自带 `LLamaSharp.dll` 与 `runtimes/win-x64/native`（只留 Windows，约 22MB） |
| 加载时机 | 一次翻译期间加载，调用结束立刻释放；不常驻、不设定时器（定时器会阻止 ALC 回收） |
| 提示词 | 混元按模型卡手工拼 `<｜hy_begin▁of▁sentence｜><｜hy_User｜>…<｜hy_Assistant｜>`；GGUF 自带的 chat_template 会被 llama.cpp 解析错 |
| 采样 | 模型卡推荐：`temperature=0.7 / top_k=20 / top_p=0.6 / repeat_penalty=1.05` |
| 分块 | 1200 字一块（上下文 4096），优先在标点处断开 |
| 专有名词 | 提示词要求产品名、代码、链接保持原文 |

插件不主动下载模型：`IsInstalled` 为假时直接返回 `Unsupported`，让用户去设置页点下载。避免执行一次翻译突然开始拉 1GB 文件。

## 六、许可提示

| 模型 | 限制 |
|---|---|
| 混元翻译 1.5（1.8B / 7B） | 腾讯混元社区协议：**欧盟 / 英国 / 韩国不可用** |
| TranslateGemma 4B | Gemma 使用条款；官方暂无稳定 GGUF 直链，清单里先留空 |
| llama.cpp（LLamaSharp 底层） | MIT |

许可文本随清单一起显示在设置页，不做自动接受。

## 七、测试

- `LocalModelManagerTests`：清单、路径、未知模型、无下载源、删除；不联网
- 真机验证记录（2026-11-20，1.8B Q4_K_M）：ModelScope 下载 1.13GB 用 98 秒；单句翻译 2.2 秒（含加载）；1750 字两段共 26 秒
- 推理无法在 CI 跑（需要模型文件），插件测试只覆盖路由与提示词，不覆盖生成质量
