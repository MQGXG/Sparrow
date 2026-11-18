# 2026-11-18 图片集输入 + OCR 单工具合并

> 状态：已确认，待实现
>
> 目标：把「图片文字识别」和「批量图片识别」合成一张卡；工具页能看见选中的图片。

---

## 一、问题

`ToolDescriptor.Accepts` 是单值，宿主按精确相等查找工具（`ToolRegistry.FindAccepting`，
截图识别依赖 `InputShape.Image`）。因此「剪贴板单图」和「多文件」只能各占一个工具，
工具墙出现两张几乎相同的 OCR 卡，而工具页又看不到自己选了哪些图。

## 二、方案

新增**图片集输入形态**，由宿主统一采集；OCR 插件只消费 `ImageSetInput`。

### 2.1 SDK（1.5，向后兼容）

```csharp
public enum InputShape { None = 0, Text, RichText, Image, Files, Selection, Images }

public sealed record ImageInput(ReadOnlyMemory<byte> Data, ImageFormat Format)
{
    public string Name { get; init; } = string.Empty;   // 文件名 / 「剪贴板图片」/「截图」
}

public sealed record ImageSetInput(IReadOnlyList<ImageInput> Images) : IToolInput;
```

不新造图片类型：单张工具继续用 `ImageInput`，批量只是多包一层。
`Images = 6` 加在末尾，不打乱已有数值。

### 2.2 宿主

工具页输入区（`Accepts == Images`）：

- 横向缩略图条：88px，文件名、单张移除
- 按钮：`添加图片…`（多选）、`粘贴剪贴板`、`清除`
- 拖入图片追加；混合文件里只收图片
- 上限：单张 20 MB、最多 50 张、总计 200 MB
- 点缩略图 → 页内浮层看大图（Esc / 点空白关闭）

拖放分流：

| 拖入内容 | 行为 |
|---|---|
| 全是图片 | 自动切到 OCR（Images）工具 |
| 含非图片、多文件 | 仍切到 Files 工具（如文件哈希） |
| 当前已是图片工具 | 追加到图片集，不跳转 |

命令面板：剪贴板图片包成 1 张 `ImageSetInput`。
截图识别：`FindAccepting(Images)`，截图包成 1 张，行框（`OcrTextLine`）逻辑不变。

### 2.3 插件

- 保留 id `sparrow.ocr.image`（历史连续），名称「图片文字识别」，插件 0.3.0
- 删除 `sparrow.ocr.batch` 与 `OcrBatchTool`，批量逻辑并入单工具
- 参数只保留 `language` + `layout`
- 输出：1 张 → 纯文本；多张 → `## 文件名` 分节，末尾列未识别文件
- 一个 worker 会话连续识别，模型只加载一次（沿用多帧协议）

## 三、不做（本轮）

PDF / Office 识别、拖拽排序、逐张重试按钮、结果逐图编辑。

## 四、验证

- 宿主：Images 构造、上限、清除、拖入分流
- 插件：1 张无标题、多张分节、单张失败不中断
- 真机：真实 worker 一次会话识别两张
- 截图回归：截图 → ImageSetInput → 行框可点行复制

## 五、影响

- 工具墙 11 → 10
- SDK 1.4 → 1.5
- 旧历史里的「批量图片识别」不再出现在最近使用（工具已不存在），历史记录本身保留
