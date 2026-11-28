using Shouldly;
using Sparrow.App.ViewModels;
using Xunit;

namespace Sparrow.Tests;

public sealed class ResultFileViewModelTests
{
    private static string TempPath(string name) => System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "sparrow-tests",
        Guid.NewGuid().ToString("N"),
        name);

    [Fact]
    public void 存在的文件_显示名字目录与大小()
    {
        var path = TempPath("a.txt");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "hello");

        try
        {
            var file = new ResultFileViewModel(path);

            file.Name.ShouldBe("a.txt");
            file.Directory.ShouldBe(System.IO.Path.GetDirectoryName(path));
            file.IsMissing.ShouldBeFalse();
            file.SizeLabel.ShouldBe("5 B");
        }
        finally
        {
            Directory.Delete(System.IO.Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void 不存在的文件_标记为缺失且不显示大小()
    {
        var file = new ResultFileViewModel(TempPath("missing.mp4"));

        file.IsMissing.ShouldBeTrue();
        file.SizeLabel.ShouldBeEmpty();
    }
}
