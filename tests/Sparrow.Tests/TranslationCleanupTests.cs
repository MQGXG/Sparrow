using Shouldly;
using Sparrow.Plugin.Translate;
using Xunit;

namespace Sparrow.Tests;

/// <summary>离线译文的提示词与清理规则：只处理小模型漏出来的英文连接词。</summary>
public sealed class TranslationCleanupTests
{
    [Fact]
    public void 中文之间夹的英文连接词被删掉()
    {
        TranslationCleanup.Apply("传递给相同的OCR工具， thus识别质量与图像工具一致")
            .ShouldBe("传递给相同的OCR工具，识别质量与图像工具一致");

        TranslationCleanup.Apply("该工具 therefore会先加载模型")
            .ShouldBe("该工具会先加载模型");
    }

    [Fact]
    public void 纯英文内容和独立英文片段不动()
    {
        // 没有中文邻居：这是正常的英文句子，不能改
        TranslationCleanup.Apply("This is thus correct.")
            .ShouldBe("This is thus correct.");

        // 代码 / 标识符保持原样
        TranslationCleanup.Apply("调用 apply() 后返回 true")
            .ShouldBe("调用 apply() 后返回 true");
    }

    [Fact]
    public void 空串和普通译文原样返回()
    {
        TranslationCleanup.Apply(string.Empty).ShouldBeEmpty();
        TranslationCleanup.Apply("这是一段正常的中文译文。").ShouldBe("这是一段正常的中文译文。");
    }
}
