using Shouldly;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>本地翻译引擎的提示词细节：目标语言名要换成混元认识的英文名。</summary>
public sealed class LocalTranslationServiceTests
{
    [Theory]
    [InlineData("English", "English")]
    [InlineData("日本語", "Japanese")]
    [InlineData("한국어", "Korean")]
    [InlineData("Русский", "Russian")]
    [InlineData("Français", "French")]
    [InlineData("Deutsch", "German")]
    [InlineData("Español", "Spanish")]
    [InlineData("Português", "Portuguese")]
    [InlineData("العربية", "Arabic")]
    [InlineData("ไทย", "Thai")]
    [InlineData("Tiếng Việt", "Vietnamese")]
    public void 常用语言都有英文名(string target, string english) =>
        LocalTranslationService.EnglishName(target).ShouldBe(english);

    [Fact]
    public void 中文保持中文指令_其他保持原样()
    {
        // 中文走中文指令，不经过英文名映射
        LocalTranslationService.EnglishName("中文").ShouldBe("中文");

        // 没配映射的语言原样传给模型，至少不会变成空串
        LocalTranslationService.EnglishName("Klingon").ShouldBe("Klingon");
    }
}
