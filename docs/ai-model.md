# AI 模型（宿主基础设施）

> AI 不是某个插件自己的实现，而是宿主能力：插件只调用 `IToolHost.Ai`，不接触 provider、端点或密钥。

## 一、支持范围

全部走 OpenAI 兼容的 `POST {baseUrl}/chat/completions`。

| 提供方 | 预设端点 | 需要 Key |
|---|---|:--:|
| OpenAI | `https://api.openai.com/v1` | ✅ |
| DeepSeek | `https://api.deepseek.com/v1` | ✅ |
| 通义千问 | `https://dashscope.aliyuncs.com/compatible-mode/v1` | ✅ |
| 智谱 | `https://open.bigmodel.cn/api/paas/v4` | ✅ |
| Moonshot | `https://api.moonshot.cn/v1` | ✅ |
| OpenRouter | `https://openrouter.ai/api/v1` | ✅ |
| Ollama（本地） | `http://localhost:11434/v1` | ❌ |
| LM Studio（本地） | `http://localhost:1234/v1` | ❌ |
| 自定义 | 任意兼容端点 | 视情况 |

## 二、密钥与隐私

- API Key 只存在 `%LOCALAPPDATA%\Sparrow\ai-key.dat`
- 使用 Windows DPAPI（当前用户）加密；换用户/换机器解不开时回退为「未配置」
- 不写入 `settings.json`、不写日志、不传给插件
- 日志只记录 provider / model / 耗时 / token 用量，不记录 prompt 和回复正文
- 网络请求只在用户执行 AI 工具时发生，不做后台轮询
- 云端内容会发送到用户填写的 Base URL；首次配置时应自行确认服务方条款

设置页只显示密钥输入框，已保存的密钥不回显；留空表示不修改，「清除密钥」单独按钮。

## 三、SDK 契约

```csharp
public interface IAiClient
{
    bool IsAvailable { get; }
    string? ModelName { get; }
    Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct);
}
```

`AiRequest`：

- `System`：系统提示词
- `Messages`：`AiRole.System / User / Assistant`
- `Image` + `ImageMimeType`：视觉模型输入，宿主负责编码成 data URL
- `Temperature`、`MaxTokens`

`AiResponse`：文本 + prompt/completion token 用量。

失败用 `AiException.Kind` 区分：

| Kind | 典型原因 |
|---|---|
| `NotConfigured` | 没启用、没密钥、端点为云端但缺 Key |
| `Unauthorized` | 401/403，Key 无效 |
| `RateLimited` | 429 |
| `Timeout` | 90 秒超时 |
| `ProviderError` | 5xx、端点 404、连接失败 |
| `InvalidResponse` | 返回不是预期 JSON / 空内容 |

## 四、写一个 AI 工具

```csharp
if (!context.Host.Ai.IsAvailable)
    return ToolResult.Unsupported("请先在「设置 › 模型」里配置 AI 模型");

var response = await context.Host.Ai.CompleteAsync(new AiRequest
{
    System = "只输出译文",
    Messages = [new AiMessage(AiRole.User, input.Text)],
    MaxTokens = 2048
}, cancellationToken);

return ToolResult.Ok(new TextPayload(response.Text));
```

插件清单声明能力：

```json
"capabilities": ["Ai"]
```

Descriptor 声明：

```csharp
Requires = PlatformCapability.Ai
```

## 五、测试

单元测试不要真的联网：

- 插件测试用假的 `IAiClient`
- 宿主 `AiClient` 测试注入 `HttpMessageHandler`，断言请求体、鉴权头、错误映射
- 密钥测试只验证「加密后文件里没有明文」

参考：`tests/Sparrow.Tests/TranslateToolTests.cs`、`AiClientTests.cs`。

## 六、限制

- 只支持非流式 `chat/completions`；流式以后按真实需求加
- 单次请求超时 90 秒
- 长文本由工具自己切块（翻译工具按标点切 3000 字）
- 不内置本地模型；本地推理由 Ollama / LM Studio 等外部进程提供
- 不做账号、云同步、模型市场

## 七、已接入工具

| 工具 | 说明 |
|---|---|
| `sparrow.text.translate` | 翻译，长文本分块 |
| `sparrow.svg.prompt` | 提示词生成 SVG；宿主清洗（去脚本/外链/事件）后用 Svg.Skia 渲染预览 |
