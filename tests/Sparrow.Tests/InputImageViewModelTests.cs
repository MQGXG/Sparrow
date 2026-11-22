using Shouldly;
using Sparrow.App.ViewModels;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Xunit;

namespace Sparrow.Tests;

public sealed class InputImageViewModelTests
{
    [Fact]
    public void 预览字节复用底层数组_不再复制整图()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var input = new ImageInput(bytes, ImageFormat.Png);
        var viewModel = new InputImageViewModel(input, sourcePath: null);

        ReferenceEquals(viewModel.PreviewBytes, bytes).ShouldBeTrue();
    }
}
