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
- **自托管**：设置页「离线模型」上方可以填**镜像根地址**（保存后立即生效）。
  下载器只换前缀、不换文件名：
  `{镜像}/HY-MT1.5-1.8B-Q4_K_M.gguf`，失败再依次回落内置源。
  代码里也可以直接给 `LocalModelManager.MirrorUrl` 赋值，同样无需重建。

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

## 五、翻译引擎（宿主共享能力）

离线推理不在插件里：`IToolHost.LocalTranslation` 是宿主能力，翻译工具和 OCR 的「翻译为」共用同一份实现。

| 项 | 做法 |
|---|---|
| 契约 | `LocalTranslationRequest`：模型 Id、文本、目标语言、语气、是否保留格式、术语表 |
| 推理库 | `LLamaSharp` + `LLamaSharp.Backend.Cpu`（**0.30+**，0.25 不认 `hunyuan-dense` 架构） |
| 分发 | 跟着宿主走，插件目录只有 0.2 MB；native 库在 `runtimes/win-x64`（只留 Windows） |
| 加载时机 | 一次翻译期间加载，调用结束立刻释放；不常驻、不设定时器（定时器会阻止回收） |
| 提示词 | 混元按模型卡手工拼 `<｜hy_begin▁of▁sentence｜><｜hy_User｜>…<｜hy_Assistant｜>`；GGUF 自带的 chat_template 会被 llama.cpp 解析错 |
| 术语表 | 混元必须放在 `<hy_User>` **之前**（system 位），放进正文会被复述成输出的一部分 |
| 采样 | 模型卡推荐：`temperature=0.7 / top_k=20 / top_p=0.6 / repeat_penalty=1.05` |
| 分块 | 1200 字一块（上下文 4096），优先在标点处断开，最多 20 块 |
| 专有名词 | 提示词要求产品名、代码、链接保持原文 |
| 输出清理 | 提示词要求「只使用中文」，并在输出侧删掉中文之间漏出的英文连接词（thus / however…） |

插件侧只描述「翻什么」，拿不到模型路径、提示词或采样参数——这些由宿主按模型决定。

**目标语言**：界面放常用 12 种（中/英/日/韩/俄/法/德/西/葡/阿/泰/越），模型本身支持 33 种；
宿主按英文名构造提示词（`Translate into Thai`），没有映射的语言原样传入。

**为什么不在每个插件里各引一次 LLamaSharp**：插件会因此各背 22 MB 的 native 库，
提示词与分块逻辑也要复制，修一个提示词就得改两处（OCR 和翻译）。

## 六、许可提示

| 模型 | 限制 |
|---|---|
| 混元翻译 1.5（1.8B / 7B） | 腾讯混元社区协议：**欧盟 / 英国 / 韩国不可用** |
| TranslateGemma 4B | Gemma 使用条款；官方暂无稳定 GGUF 直链，清单里先留空 |
| llama.cpp（LLamaSharp 底层） | MIT |

许可文本随清单一起显示在设置页，不做自动接受。

## 七、测试

- `LocalModelManagerTests`：清单、路径、未知模型、无下载源、删除、镜像优先级；不联网
- 真机验证记录（2026-11-20，1.8B Q4_K_M）：ModelScope 下载 1.13GB 用 98 秒；单句翻译 2.2 秒（含加载）；1750 字两段共 26 秒
- 推理无法在 CI 跑（需要模型文件），插件测试只覆盖路由与提示词，不覆盖生成质量
- **7B 未实测**：需要 4.6GB 模型 + 约 6GB 空余内存。本机当时只剩 7.8GB 可用内存，
  加载会触发换页，所以没有硬验；代码路径与 1.8B 完全一致，只差模型文件和内存要求

## 八、谁在用

| 插件 | 用法 |
|---|---|
| `sparrow.plugin.translate` | 「引擎」选离线时调 `LocalTranslation`；明确选了离线且模型没下载 → 提示去设置页 |
| `sparrow.plugin.ocr` | 「翻译为」优先用在线 AI；没配 AI 时自动用第一个已下载的本地模型，两者都没有才标注「未翻译」 |

插件都不主动下载模型：`IsInstalled` 为假时直接返回提示，让用户去设置页点下载——
避免执行一次翻译突然开始拉 1GB 文件。
