# 开发一个工具插件（Tool Plugin Development）

> 这份文档是**唯一**的新工具入口。
>
> 目标：**新增一个工具 = 建一个插件目录，不改宿主一行代码。**

---

## 一、动手前先回答三问

```text
1. 它的输入形态是什么？（Text / Image / FileSet / Nothing）
2. 它的输出形态是什么？（Text / Svg / Image / FileSet / Table）
3. 用户为什么不用更顺手的方式做这件事？
```

第 3 问答不上，不要做。先加到 [`tool-catalog.md`](tool-catalog.md) 标 `P2`。

---

## 二、手工骨架（三步）

`dotnet new sparrow-tool` 模板还没做（排在 M2）。现在手工建，三步。

产出：

```text
plugins/sparrow.plugin.<分类>-<动作>/
├─ Sparrow.Plugin.<分类>.<动作>.csproj
├─ plugin.json
└─ <Tool>.cs
```

**目录名必须等于 `plugin.json` 的 `id`。** 扫描器只认这个。

---

## 三、手工骨架（四步）

### 3.1 csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>Sparrow.Plugin.Json.Format</AssemblyName>
    <RootNamespace>Sparrow.Plugin.Json.Format</RootNamespace>
    <!-- ★ 等于目录名，也等于 plugin.json 的 id；构建时据此决定复制到 plugins/ 下哪个目录 -->
    <PluginId>sparrow.plugin.json-format</PluginId>
  </PropertyGroup>

  <ItemGroup>
    <!-- ★ 关键：不能把 SDK 拷进插件目录，否则 ITool 类型会对不上 -->
    <ProjectReference Include="..\..\src\Sparrow.Plugin.Sdk\Sparrow.Plugin.Sdk.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>

  <ItemGroup>
    <None Include="plugin.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

`TargetFramework` / `Nullable` / `ImplicitUsings` 全部由根目录 `Directory.Build.props` 提供，插件不要重复声明。

### 3.2 plugin.json

```json
{
  "id": "sparrow.plugin.json-format",
  "name": "JSON 格式化",
  "version": "1.0.0",
  "sdkVersion": "1.0",
  "entryAssembly": "Sparrow.Plugin.Json.Format.dll",
  "author": "sparrow",
  "description": "格式化、压缩、校验 JSON，出错时定位到行列",
  "capabilities": [],
  "tools": ["sparrow.json.format"]
}
```

**目录名必须等于 `id`。** 加载器只认这个。

### 3.3 Plugin.cs

```csharp
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.Plugin.Json.Format;

public sealed class JsonFormatTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new()
    {
        Id = "sparrow.json.format",
        Name = "JSON 格式化",
        Category = "json",
        Description = "格式化、压缩或校验 JSON，出错时定位到行列",
        Keywords = ["json", "格式化", "压缩", "美化", "校验", "beautify"],
        Accepts = InputShape.Text,
        Produces = OutputShape.Text,
        Requires = PlatformCapability.None,
        Order = 10,
        Parameters =
        [
            new ToolParameter { Key = "mode",   Label = "模式", Kind = ParameterKind.Choice,
                                Choices = ["格式化", "压缩", "校验"], Default = "格式化" },
            new ToolParameter { Key = "indent", Label = "缩进", Kind = ParameterKind.Choice,
                                Choices = ["2", "4", "Tab"],        Default = "2" }
        ]
    };

    public Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (context.Input is not TextInput input)
            return Task.FromResult(ToolResult.InvalidInput("需要文本输入"));

        var mode = context.GetParameter("mode", "格式化");
        var indent = context.GetParameter("indent", 2);

        if (!JsonDocument.TryParse(input.Text, out var doc, out var parseError))
            return Task.FromResult(ToolResult.InvalidInput(parseError!));

        var text = mode switch
        {
            "压缩" => JsonWriter.Minify(doc),
            "校验" => "JSON 合法",
            _      => JsonWriter.Pretty(doc, indent)
        };

        return Task.FromResult(ToolResult.Ok(new TextPayload(text)));
    }
}
```

### 3.4 就这样，没有第四步

**不需要注册。** 宿主启动时扫描 `plugins/`，读清单，自动发现。

这正是插件化的直接收益：**加工具的最后一公里消失了。** 早期设计的方案里还需要在 `ModuleRegistration.cs` 里加一行注册，而那一行恰恰就是「宿主知道具体工具」的口子——现在这条路被彻底堵上了。

---

## 四、契约清单

### 4.1 必须做

| 要求 | 原因 |
|---|---|
| `Descriptor.Id` 用 `sparrow.<分类>.<动作>` | 会进历史库和设置，不能改 |
| `Descriptor.Id` 必须出现在 `plugin.json` 的 `tools` 里 | 加载器会校验，不匹配则该工具被忽略 |
| `Keywords` 至少 3 个，含中文口语词 | 面板搜索靠它，用户不会输入正式名 |
| 输入形态不符时返回 `InvalidInput` | 不要抛异常，不要把 null 往下传 |
| `ExecuteAsync` 支持 `CancellationToken` | 长任务必须能被取消 |
| 参数走 `Descriptor.Parameters` | 不要自己读配置文件 |
| 结果用 `Payload` 表达 | 不要返回裸字符串 |
| 同一输入重复执行结果一致 | 无状态，便于并发和卸载 |

### 4.2 禁止做

```text
✗ 引用 Avalonia（插件不拥有 UI）
✗ 引用 Sparrow.Core / Sparrow.App（只看得到 SDK）
✗ 弹 MessageBox / 打开窗口
✗ 直接读写文件（返回 FileSetPayload，由宿主落盘）
✗ 直接访问剪贴板（输入已由 ToolContext 提供）
✗ new HttpClient()（用 IToolHost.Http，受策略控制）
✗ 注册全局热键 / 托盘 / 文件关联（宿主独占）
✗ 订阅宿主的静态事件（会导致 ALC 无法卸载）
✗ 起后台 Timer / Thread（同上）
✗ 在静态字段里缓存大对象（同上）
```

最后三条是硬约束：**只要插件在 ALC 之外还存活任何引用，卸载就会失败。**

---

## 五、插件能用什么

```csharp
public interface IToolHost
{
    ILogger Logger { get; }
    IToolSettings Settings { get; }           // 按 toolId 隔离
    IHttpClientAccess Http { get; }           // 可被用户策略禁用
}
// 进度用 context.Progress，不是 Host 的能力
```

就这三样。**没有文件系统、没有数据库、没有剪贴板、没有窗口。**

| 需求 | 正确做法 | 错误做法 |
|---|---|---|
| 记日志 | `Host.Logger.Info(...)` | `File.AppendAllText` |
| 存配置 | `Host.Settings.Get/Set` | 自己读 JSON 文件 |
| 报进度 | `context.Progress?.Report(...)` | 直接改 UI |
| 调接口 | `Host.Http.SendAsync` | `new HttpClient()` |
| 产出文件 | 返回 `FileSetPayload` | 自己写磁盘 |
| 读剪贴板 | 不需要，`ToolContext` 已给 | 调 Win32 API |
| 显示进度条 | 不需要，宿主根据 `Progress` 渲染 | 引用 Avalonia |

---

## 六、参数类型

```csharp
public enum ParameterKind { Text, Multiline, Number, Toggle, Choice, FilePath, Color }

public sealed record ToolParameter
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public ParameterKind Kind { get; init; }
    public string? Default { get; init; }
    public string[] Choices { get; init; } = [];
    public string? Hint { get; init; }
}
```

**参数上限 5 个。** 超过说明这个工具在做两件事，拆开。

参数的持久化由宿主负责（按 `pluginId + toolId + key` 存最近值），插件不关心。

---

## 七、结果构造

```csharp
ToolResult.Ok(new TextPayload(text))
ToolResult.InvalidInput("JSON 语法错误，第 3 行第 12 列")
ToolResult.Failed(ex.Message, ex.ToString())
ToolResult.Cancelled()
```

| 场景 | 用哪个 |
|---|---|
| 输入类型不对 | `InvalidInput` |
| 输入内容非法（语法错） | `InvalidInput` + 定位信息 |
| 内部异常 | `Failed`（宿主记日志、显示「插件出错」） |
| 用户取消 | `Cancelled`（不记日志，不报错） |

**区分 `InvalidInput` 和 `Failed` 很重要**：前者是用户的问题（告诉他怎么改），后者是我们的问题（显示错误码 + 日志入口）。

---

## 八、测试

插件可以脱离宿主测试——因为它只依赖 SDK。

```csharp
[Fact]
public async Task 正常输入_返回格式化结果()
{
    var tool = new JsonFormatTool();
    var ctx = new ToolContext
    {
        Input = new TextInput("""{"b":2,"a":1}"""),
        Parameters = new Dictionary<string, string> { ["mode"] = "格式化", ["indent"] = "2" }
    };

    var result = await tool.ExecuteAsync(ctx, default);

    result.Success.ShouldBeTrue();
    result.Payload.ShouldBeOfType<TextPayload>().Text.ShouldContain("\"a\": 1");
}

[Fact]
public async Task 非法输入_返回InvalidInput而不是抛异常()
{
    var result = await new JsonFormatTool().ExecuteAsync(
        new ToolContext { Input = new TextInput("{oops") }, default);

    result.Success.ShouldBeFalse();
    result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
}
```

每个插件至少三个测试：

```text
[ ] 正常输入 → 预期结果
[ ] 空输入 / 错误输入 → InvalidInput，不抛异常
[ ] 超长输入 → 在超时内返回或被取消
```

---

## 九、构建与产出

```bash
dotnet build                                              # 构建整个解决方案
dotnet test --filter "FullyQualifiedName~Base64"          # 只跑某个插件的测试
```

**必须构建整个解决方案**：插件不通过 App 的 `ProjectReference` 构建（那样加插件就得改宿主）。插件构建完会由 `plugins/Directory.Build.targets` 自动同步到宿主输出目录：

```text
src/Sparrow.App/bin/Debug/net10.0/plugins/
├─ sparrow.plugin.json-format/
│  ├─ Sparrow.Plugin.Json.Format.dll
│  ├─ plugin.json
│  └─ .deps.json / .pdb
└─ sparrow.plugin.dev-base64/
   └─ ...
```

插件项目只需要声明 `<PluginId>`，不写任何路径，也不碰宿主 csproj。

检查 SDK 是否混入：

```bash
dir /s src\Sparrow.App\bin\Debug\net10.0\plugins\Sparrow.Plugin.Sdk.dll
# 应该没有输出
```

如果出现了 `Sparrow.Plugin.Sdk.dll`：后果是插件被扫到但一个工具都不注册（`is ITool` 为 false），**且没有任何错误日志**。加载器的 `Load()` 会兜底拦截，但构建产物本身不该有它。

---

## 十、本地验证

```text
1. 构建插件
2. 把输出目录整体拷到 %LOCALAPPDATA%\Sparrow\plugins\sparrow.plugin.json-format\
   （或直接用 build/install-plugins.ps1 脚本）
3. 重启 Sparrow
4. Ctrl+Alt+S → 输入 "json" → 回车
5. 检查：结果正确、参数可切换、历史有记录、日志无警告
```

**调试加载失败**：日志里搜 `plugin`：

```text
[WRN] Plugin 'sparrow.plugin.json-format' manifest invalid: ...
[WRN] Plugin 'sparrow.plugin.json-format' skipped: sdkVersion 2.0 incompatible
[ERR] Plugin 'sparrow.plugin.json-format' loaded but no ITool found
```

第三行几乎总是「SDK dll 被拷进插件目录」导致的。

---

## 十一、常见错误

| 现象 | 原因 |
|---|---|
| 面板里没有这个工具 | 目录名 ≠ `plugin.json` 的 `id` |
| 同上，且日志有 "no ITool found" | **SDK dll 被拷进了插件目录**（铁律 1） |
| 面板里灰显 | `Requires` 声明了当前平台不具备的能力 |
| 参数改了没生效 | 用了 `Parameters["x"]`（会抛），应 `GetParameter` |
| 切换工具后结果还在 | 宿主的工作区状态没按 `toolId` 重置 |
| 禁用插件后内存没降 | 插件里有静态事件订阅 / Timer / 静态缓存 |
| 大文本卡死 UI | 在 `ExecuteAsync` 里同步做 CPU 密集计算，应 `await Task.Run(...)` |
| 两个插件行为互相影响 | 在同一个 ALC 里了（加载器 bug） |

---

## 十二、什么时候该拆到独立进程

只有一种情况：**需要加载大体积原生库或模型，且会显著推高常驻内存，或存在原生崩溃风险。**

目前只有 OCR 符合（Paddle 78MB + OpenCV 38MB，且无 arm64）。

**注意**：拆进程的是**重活**，不是插件本身。OCR 插件仍然是普通插件，它只是把识别委托给 `Sparrow.Ocr.Host` 子进程。

不要因为「感觉会很慢」就提前拆进程——进程间传图的成本（4K 截图 25MB，序列化+拷贝 ≈ 100ms+）比想象中高。
