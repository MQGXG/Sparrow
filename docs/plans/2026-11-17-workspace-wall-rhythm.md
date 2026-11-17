# 工具墙布局节奏与导航修复 · 实现计划

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 修复工作区导航卡死（最近页无法回工具墙），并把工具墙从"等大卡片网格"改造成「大卡 + 分组小卡」的节奏布局。

**Architecture:** VM 层把"分类过滤"改为"分组折叠"（`CollapsedCategories` + `ToolGroupViewModel.IsExpanded`），新增「最近使用」大卡属性；View 层把切换 tab 提到共享层、删除冗余「最近执行」条、按分组渲染墙、补 Esc 返回。

**Tech Stack:** Avalonia 12 + CommunityToolkit.Mvvm + xUnit + Avalonia.Headless

---

## Task 1: VM —— 分类从「过滤」改为「折叠」

**Files:**
- Modify: `src/Sparrow.App/ViewModels/ToolGroupViewModel.cs`
- Modify: `src/Sparrow.App/ViewModels/WorkspaceViewModel.cs:412-471`
- Modify: `tests/Sparrow.Tests/WorkspaceViewModelTests.cs:145-164`

**Step 1: 改测试（先写新语义）**

把 `选择分类_书签条同步更新_列表只剩该分类` 整个替换为：

```csharp
[Fact]
public async Task 选择分类_书签条同步更新_其余分组折叠()
{
    using var root = new TempPluginRoot();
    using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
    var viewModel = Create(catalog, new FakeHistory());

    await viewModel.ReloadAsync();

    viewModel.Categories.First().Name.ShouldBe("全部");
    viewModel.Categories.First().IsSelected.ShouldBeTrue();

    var target = viewModel.Categories.First(category => category.Name != "全部");
    viewModel.SelectCategory(target.Name);

    viewModel.Categories.Single(category => category.Name == target.Name).IsSelected.ShouldBeTrue();
    viewModel.Categories.Single(category => category.Name == "全部").IsSelected.ShouldBeFalse();

    // 分类不再从列表里移除：两组都在，选中组展开、其余折叠
    viewModel.Groups.Count.ShouldBe(2);
    viewModel.Groups.Single(group => group.Category == target.Name).IsExpanded.ShouldBeTrue();
    viewModel.Groups.Single(group => group.Category != target.Name).IsExpanded.ShouldBeFalse();
    viewModel.VisibleTools.Count.ShouldBe(2);
}

[Fact]
public async Task 组标题点击_折叠状态可切换()
{
    using var root = new TempPluginRoot();
    using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
    var viewModel = Create(catalog, new FakeHistory());

    await viewModel.ReloadAsync();

    var group = viewModel.Groups.First();
    group.IsExpanded.ShouldBeTrue();

    viewModel.ToggleGroup(group.Category);
    group.IsExpanded.ShouldBeFalse();

    viewModel.ToggleGroup(group.Category);
    group.IsExpanded.ShouldBeTrue();
}

[Fact]
public async Task 搜索时_所有分组强制展开()
{
    using var root = new TempPluginRoot();
    using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
    var viewModel = Create(catalog, new FakeHistory());

    await viewModel.ReloadAsync();

    var category = viewModel.Groups.First().Category;
    viewModel.ToggleGroup(category);
    viewModel.Groups.First(group => group.Category == category).IsExpanded.ShouldBeFalse();

    viewModel.ToolQuery = "json";
    viewModel.Groups.ShouldAllBe(group => group.IsExpanded);

    viewModel.ToolQuery = string.Empty;
    viewModel.Groups.Single(group => group.Category == category).IsExpanded.ShouldBeFalse();
}
```

**Step 2: 跑测试确认失败**

Run: `dotnet test tests/Sparrow.Tests --nologo -v q`
Expected: 编译失败（`IsExpanded` / `ToggleGroup` 不存在）

**Step 3: 给 ToolGroupViewModel 加展开状态**

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sparrow.App.Services;

namespace Sparrow.App.ViewModels;

/// <summary>
/// 工作区工具墙的一个分类分组。分类来自 <c>ToolDescriptor.Category</c>，宿主不硬编码任何工具名。
/// </summary>
public sealed partial class ToolGroupViewModel : ObservableObject
{
    public ToolGroupViewModel(string category, IEnumerable<ToolItemViewModel> tools)
    {
        Category = category;
        Tools = new ObservableCollection<ToolItemViewModel>(tools);
    }

    public string Category { get; }

    public string DisplayName => ToolCategories.Display(Category);

    public ObservableCollection<ToolItemViewModel> Tools { get; }

    /// <summary>组内卡片是否展开；由 WorkspaceViewModel 统一计算（搜索强制展开 / chip 联动 / 手动折叠）。</summary>
    [ObservableProperty]
    private bool _isExpanded = true;
}
```

**Step 4: 改 WorkspaceViewModel 的折叠逻辑**

在字段区（`_allHistory` 附近）加：

```csharp
/// <summary>手动折叠的分类；chip 选中时由 chip 规则接管。</summary>
private readonly HashSet<string> _collapsedCategories = new(StringComparer.Ordinal);
```

加公开方法（放在 `SelectCategory` 之后）：

```csharp
/// <summary>点组标题：折叠/展开该组。手动操作即退出 chip 联动模式，避免两套规则打架。</summary>
public void ToggleGroup(string category)
{
    SelectedCategory = null;

    if (!_collapsedCategories.Add(category))
        _collapsedCategories.Remove(category);

    UpdateGroupExpansion();
}
```

`OnToolQueryChanged` 保持调用 `RefilterTools`；`OnSelectedCategoryChanged` 改为：

```csharp
partial void OnSelectedCategoryChanged(string? value)
{
    // 分类不再过滤列表，只决定展开哪一组
    UpdateCategorySelection();
    UpdateGroupExpansion();
}
```

`RefilterTools` 去掉分类过滤分支（只按搜索词过滤），末尾补 `UpdateGroupExpansion()`：

```csharp
private void RefilterTools()
{
    var query = ToolQuery.Trim();
    var items = _allTools;
    if (query.Length > 0)
    {
        items = items.Where(entry =>
            entry.Descriptor.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            entry.Descriptor.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            entry.Descriptor.Category.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    var list = items.ToList();
    var tools = list.Select(entry => new ToolItemViewModel(entry)).ToList();

    Groups.Clear();
    VisibleTools.Clear();

    foreach (var tool in tools)
        VisibleTools.Add(tool);

    foreach (var group in tools
                 .GroupBy(tool => tool.CategoryLabel)
                 .OrderBy(group => group.Key, StringComparer.Ordinal))
    {
        Groups.Add(new ToolGroupViewModel(group.Key, group));
    }

    HasTools = list.Count > 0;
    UpdateGroupExpansion();
    OnPropertyChanged(nameof(EmptyToolMessage));
}

/// <summary>按「搜索 &gt; chip &gt; 手动折叠」三级规则计算每组展开状态。</summary>
private void UpdateGroupExpansion()
{
    var hasQuery = ToolQuery.Trim().Length > 0;

    foreach (var group in Groups)
    {
        group.IsExpanded = hasQuery
            || (SelectedCategory is null
                ? !_collapsedCategories.Contains(group.Category)
                : group.Category == SelectedCategory);
    }
}
```

**Step 5: 跑测试确认通过**

Run: `dotnet test tests/Sparrow.Tests --nologo -v q`
Expected: PASS（含新测试）

**Step 6: Commit**

```bash
git add src/Sparrow.App/ViewModels/ToolGroupViewModel.cs src/Sparrow.App/ViewModels/WorkspaceViewModel.cs tests/Sparrow.Tests/WorkspaceViewModelTests.cs
git commit -m "feat(workspace): 分类改为分组折叠，组标题可点击"
```

---

## Task 2: VM —— 墙顶大卡数据 + 搜索回墙

**Files:**
- Modify: `src/Sparrow.App/ViewModels/WorkspaceViewModel.cs:109`（`OnToolQueryChanged`）
- Modify: `src/Sparrow.App/ViewModels/WorkspaceViewModel.cs:476-499`（`RebuildRecentTools`）
- Modify: `src/Sparrow.App/ViewModels/WorkspaceViewModel.cs:745-753`（`ClearHistory`）
- Test: `tests/Sparrow.Tests/WorkspaceViewModelTests.cs`

**Step 1: 写失败测试**

```csharp
[Fact]
public async Task 有历史时_大卡显示最近工具与摘要()
{
    using var root = new TempPluginRoot();
    using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());

    var json = catalog.GetRegistry().Search("json").ShouldHaveSingleItem();
    var history = new FakeHistory();
    history.Entries.Add(new HistoryEntry(
        "h1", json.Descriptor.Id, json.Descriptor.Name,
        "{ \"a\": 1 }", "", DateTimeOffset.Now, 12));

    var viewModel = Create(catalog, history);
    await viewModel.ReloadAsync();

    viewModel.HasFeaturedTool.ShouldBeTrue();
    viewModel.FeaturedTool!.Tool.Name.ShouldBe(json.Descriptor.Name);
    viewModel.FeaturedSummary.ShouldContain("\"a\": 1");

    viewModel.ClearHistory();

    viewModel.HasFeaturedTool.ShouldBeFalse();
    viewModel.FeaturedSummary.ShouldBeEmpty();
}

[Fact]
public async Task 最近页输入搜索词_回到工具墙()
{
    using var root = new TempPluginRoot();
    using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
    var viewModel = Create(catalog, new FakeHistory());

    await viewModel.ReloadAsync();
    viewModel.ShowRecent();
    viewModel.ToolQuery = "json";

    viewModel.IsRecentOpen.ShouldBeFalse();
    viewModel.IsWallVisible.ShouldBeTrue();
}
```

**Step 2: 跑测试确认失败**

Run: `dotnet test tests/Sparrow.Tests --nologo -v q`
Expected: 编译失败（`HasFeaturedTool` 等不存在）

**Step 3: 实现**

`OnToolQueryChanged`：

```csharp
partial void OnToolQueryChanged(string value)
{
    // 最近页输入搜索词 = 想找别的工具，直接回工具墙看结果
    if (value.Length > 0)
        IsRecentOpen = false;

    RefilterTools();
}
```

加属性（放在 `RecentTools` 声明之后）：

```csharp
/// <summary>墙顶大卡：最近执行过的工具；没有历史时为 null。</summary>
public RecentToolViewModel? FeaturedTool => RecentTools.Count > 0 ? RecentTools[0] : null;

public bool HasFeaturedTool => FeaturedTool is not null;

/// <summary>大卡右侧的上次结果摘要，前 60 字。</summary>
public string FeaturedSummary
{
    get
    {
        if (_allHistory.Count == 0)
            return string.Empty;

        var output = _allHistory[0].Entry.Output;
        return output.Length <= 60 ? output : output[..60] + "…";
    }
}
```

`RebuildRecentTools` 末尾与 `ClearHistory` 末尾各加：

```csharp
NotifyFeatured();
```

私有方法：

```csharp
private void NotifyFeatured()
{
    OnPropertyChanged(nameof(FeaturedTool));
    OnPropertyChanged(nameof(HasFeaturedTool));
    OnPropertyChanged(nameof(FeaturedSummary));
}
```

**Step 4: 跑测试确认通过**

Run: `dotnet test tests/Sparrow.Tests --nologo -v q`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Sparrow.App/ViewModels/WorkspaceViewModel.cs tests/Sparrow.Tests/WorkspaceViewModelTests.cs
git commit -m "feat(workspace): 墙顶大卡数据与搜索回墙"
```

---

## Task 3: View —— tab 行提共享层 + 搜索框常驻 + Esc 返回

**Files:**
- Modify: `src/Sparrow.App/Views/WorkspaceWindow.axaml:59-74, 80-120`
- Modify: `src/Sparrow.App/Views/WorkspaceWindow.axaml.cs`
- Test: `tests/Sparrow.Tests/WorkspaceWindowTests.cs`（新建）

**Step 1: 写失败测试（headless）**

新建 `tests/Sparrow.Tests/WorkspaceWindowTests.cs`：

```csharp
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Shouldly;
using Sparrow.App;
using Sparrow.App.History;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.App.ViewModels;
using Sparrow.App.Views;

namespace Sparrow.Tests;

/// <summary>
/// 工作区窗口的导航回归：tab 行必须常驻，Esc 必须能从工具台返回。
/// 这两条曾经让用户「用了一个工具就回不到工具墙」。
/// </summary>
public sealed class WorkspaceWindowTests
{
    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; set; }
        public byte[]? ImagePng { get; set; }
        public string? GetText() => Text;
        public byte[]? GetImagePng() => ImagePng;
        public bool SetText(string text) { Text = text; return true; }
        public bool SetImagePng(byte[] png) { ImagePng = png; return true; }
    }

    private static (WorkspaceWindow Window, WorkspaceViewModel ViewModel, ToolCatalog Catalog) Create()
    {
        var log = new NullLog();
        var rootPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        var catalog = new ToolCatalog(rootPath, log, new AppSettings());
        var store = new SettingsStore(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N") + ".json"),
            log);
        var settingsViewModel = new SettingsViewModel(catalog, store, new StartupService(log), new AppSettings());

        var viewModel = new WorkspaceViewModel(catalog, new FakeClipboard(), log, new NullHistoryStore(), settingsViewModel);
        var window = new WorkspaceWindow { DataContext = viewModel };
        window.Show();

        return (window, viewModel, catalog);
    }

    [AvaloniaFact]
    public void 最近页_切换tab行仍然可见()
    {
        var (window, viewModel, catalog) = Create();
        try
        {
            viewModel.ShowRecent();

            var tabRow = window.FindControl<Grid>("WallTabRow");
            tabRow.ShouldNotBeNull("最近页必须保留「工具 / 最近使用」切换入口");
            tabRow.IsVisible.ShouldBeTrue();
        }
        finally
        {
            catalog.Dispose();
        }
    }

    [AvaloniaFact]
    public void 工具台_Esc返回工具墙()
    {
        var (window, viewModel, catalog) = Create();
        try
        {
            viewModel.IsDetailOpen = true;

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);

            viewModel.IsDetailOpen.ShouldBeFalse();
            viewModel.IsWallVisible.ShouldBeTrue();
        }
        finally
        {
            catalog.Dispose();
        }
    }

    [AvaloniaFact]
    public void 设置页_Esc不抢返回()
    {
        var (window, viewModel, catalog) = Create();
        try
        {
            viewModel.IsDetailOpen = true;
            viewModel.IsSettingsVisible = true;

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);

            viewModel.IsDetailOpen.ShouldBeTrue();
        }
        finally
        {
            catalog.Dispose();
        }
    }
}
```

**Step 2: 跑测试确认失败**

Run: `dotnet test tests/Sparrow.Tests --nologo -v q`
Expected: FAIL —— `FindControl<Grid>("WallTabRow")` 为 null（还没命名/还没提层）

**Step 3: 改 XAML 结构**

把主体容器改成两行：共享 tab 行（`Grid.Row=0`）+ 内容（`Grid.Row=1`）。

原结构（第 80-120 行）：
```xml
      <Grid Grid.Row="2" Margin="24,16,24,20">
        <Grid IsVisible="{Binding !IsDetailOpen}">
          <ScrollViewer IsVisible="{Binding IsWallVisible}">
            <StackPanel>
              <Grid ColumnDefinitions="Auto,*,Auto" Margin="0,0,0,16">  ← tab 行
                ...
              </Grid>
```

新结构：
```xml
      <Grid Grid.Row="2" Margin="24,16,24,20" RowDefinitions="Auto,*">

        <!-- 切换行提到共享层：最近页也能切回工具墙（原先卡死的根因） -->
        <Grid x:Name="WallTabRow"
              Grid.Row="0"
              ColumnDefinitions="Auto,*,Auto"
              Margin="0,0,0,16"
              IsVisible="{Binding !IsDetailOpen}">
          ... 原 tab 行内容原样搬入（两个 hud-tab + TOOLS 读出）...
        </Grid>

        <Grid Grid.Row="1">
          <Grid IsVisible="{Binding !IsDetailOpen}">
            <ScrollViewer IsVisible="{Binding IsWallVisible}">
              <StackPanel>
                <!-- 原 tab 行位置不再有内容；chips 起头 -->
```

并把外层 detail Grid 移到 `Grid.Row="1"` 容器内。

搜索框（第 60-67 行）去掉 `IsVisible="{Binding !IsRecentOpen}"`。

**Step 4: 加 Esc 处理（code-behind）**

构造函数加：

```csharp
// Esc：工具台返回工具墙（隧道阶段，避免被输入框吃掉）
AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
```

方法：

```csharp
private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
{
    if (e.Key != Key.Escape)
        return;

    if (DataContext is not WorkspaceViewModel viewModel)
        return;

    // 设置页有自己的「← 返回」，Esc 不抢
    if (viewModel.IsSettingsVisible)
        return;

    if (viewModel.IsDetailOpen)
    {
        viewModel.CloseDetail();
        e.Handled = true;
    }
}
```

**Step 5: 跑测试确认通过**

Run: `dotnet test tests/Sparrow.Tests --nologo -v q`
Expected: PASS

**Step 6: Commit**

```bash
git add src/Sparrow.App/Views/WorkspaceWindow.axaml src/Sparrow.App/Views/WorkspaceWindow.axaml.cs tests/Sparrow.Tests/WorkspaceWindowTests.cs
git commit -m "fix(workspace): tab 行常驻共享层 + Esc 返回，修复导航卡死"
```

---

## Task 4: 删除工具墙「最近执行」条

**Files:**
- Modify: `src/Sparrow.App/Views/WorkspaceWindow.axaml`（「最近执行」整块，原第 218-272 行）

**Step 1: 删除**

找到注释 `<!-- 最近执行：填在墙下面，让下半页有真实内容 -->` 起始的整个 `<StackPanel ... IsVisible="{Binding HasHistory}">…</StackPanel>`，整块删除。
「最近使用」页里的「执行记录」区块保持不动。

**Step 2: 构建验证**

Run: `dotnet build Sparrow.slnx --nologo -v q`
Expected: 0 errors

**Step 3: Commit**

```bash
git add src/Sparrow.App/Views/WorkspaceWindow.axaml
git commit -m "refactor(workspace): 删除工具墙底部冗余的最近执行条"
```

---

## Task 5: 分组渲染 + 组标题样式

**Files:**
- Modify: `src/Sparrow.App/Views/WorkspaceWindow.axaml:146-215`（扁平工具网格 → 分组）
- Modify: `src/Sparrow.App/Views/WorkspaceWindow.axaml.cs`
- Modify: `src/Sparrow.App/Styles/Theme.axaml`（组标题样式）

**Step 1: XAML —— 扁平列表换成分组列表**

把原 `<ItemsControl ItemsSource="{Binding VisibleTools}">…</ItemsControl>`（工具卡片模板）替换为：

```xml
<ItemsControl ItemsSource="{Binding Groups}">
  <ItemsControl.ItemTemplate>
    <DataTemplate x:DataType="vm:ToolGroupViewModel">
      <StackPanel Margin="0,0,0,6">

        <!-- 组标题：琥珀刻度 + 分类名 + 数量；点击折叠/展开 -->
        <Button Classes="group-header"
                Tag="{Binding}"
                Click="OnGroupHeaderClick"
                HorizontalAlignment="Stretch"
                HorizontalContentAlignment="Left">
          <StackPanel Orientation="Horizontal" Spacing="8">
            <Border Classes="hud-tick" />
            <TextBlock Text="{Binding DisplayName}"
                       FontSize="13"
                       FontWeight="SemiBold"
                       Foreground="{StaticResource TextPrimaryBrush}"
                       VerticalAlignment="Center" />
            <TextBlock Classes="num"
                       Text="{Binding Tools.Count}"
                       Margin="4,0,0,0"
                       VerticalAlignment="Center" />
            <Path Width="8" Height="5"
                  Margin="6,0,0,0"
                  VerticalAlignment="Center"
                  Stroke="{StaticResource TextMutedBrush}"
                  StrokeThickness="1.5"
                  StrokeLineCap="Round"
                  StrokeJoin="Round"
                  IsVisible="{Binding IsExpanded}"
                  Data="M0,0 L4,4 L8,0" />
            <Path Width="8" Height="5"
                  Margin="6,0,0,0"
                  VerticalAlignment="Center"
                  Stroke="{StaticResource TextMutedBrush}"
                  StrokeThickness="1.5"
                  StrokeLineCap="Round"
                  StrokeJoin="Round"
                  IsVisible="{Binding !IsExpanded}"
                  Data="M0,0 L4,4 L0,8" />
          </StackPanel>
        </Button>

        <ItemsControl ItemsSource="{Binding Tools}"
                      IsVisible="{Binding IsExpanded}"
                      Margin="0,8,0,0">
          <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate>
              <WrapPanel Orientation="Horizontal" />
            </ItemsPanelTemplate>
          </ItemsControl.ItemsPanel>
          <ItemsControl.ItemTemplate>
            <DataTemplate x:DataType="vm:ToolItemViewModel">
              ... 原工具卡片 Button（tool-card，236×136）原样保留 ...
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
      </StackPanel>
    </DataTemplate>
  </ItemsControl.ItemTemplate>
</ItemsControl>
```

**Step 2: code-behind 加组标题点击**

```csharp
private void OnGroupHeaderClick(object? sender, RoutedEventArgs e)
{
    if (sender is Button { Tag: ToolGroupViewModel group } && DataContext is WorkspaceViewModel viewModel)
        viewModel.ToggleGroup(group.Category);
}
```

**Step 3: Theme 加组标题样式**

在 `TextBlock.stat` 之前插入：

```xml
<!-- 分组标题：透明底、hover 才浮一层；点击折叠 -->
<Style Selector="Button.group-header">
  <Setter Property="Background" Value="Transparent" />
  <Setter Property="BorderThickness" Value="0" />
  <Setter Property="CornerRadius" Value="4" />
  <Setter Property="Padding" Value="6,4" />
  <Setter Property="HorizontalContentAlignment" Value="Left" />
</Style>

<Style Selector="Button.group-header:pointerover /template/ ContentPresenter#PART_ContentPresenter">
  <Setter Property="Background" Value="{StaticResource SurfaceHoverBrush}" />
</Style>
```

**Step 4: 构建 + 全量测试**

Run: `dotnet build Sparrow.slnx --nologo -v q`
Expected: 0 errors
Run: `dotnet test tests/Sparrow.Tests --nologo -v q`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Sparrow.App/Views/WorkspaceWindow.axaml src/Sparrow.App/Views/WorkspaceWindow.axaml.cs src/Sparrow.App/Styles/Theme.axaml
git commit -m "feat(workspace): 工具墙按分类分组渲染，组标题可折叠"
```

---

## Task 6: 墙顶大卡

**Files:**
- Modify: `src/Sparrow.App/Views/WorkspaceWindow.axaml`（chips 之后、分组列表之前）
- Modify: `src/Sparrow.App/Views/WorkspaceWindow.axaml.cs`

**Step 1: XAML 大卡**

在分类 chips 的 `</ItemsControl>` 之后插入：

```xml
<!-- 大卡：最近执行过的工具，双倍宽；没有历史时整块不显示 -->
<Button IsVisible="{Binding HasFeaturedTool}"
        Tag="{Binding FeaturedTool}"
        Click="OnFeaturedToolClick"
        Classes="tool-card"
        Width="488"
        Height="136"
        Margin="0,0,16,16"
        Padding="0"
        HorizontalContentAlignment="Stretch"
        VerticalContentAlignment="Stretch">
  <Panel>
    <Path Classes="frame" HorizontalAlignment="Left" VerticalAlignment="Top" Data="M0,11 L0,0 L11,0" />
    <Path Classes="frame" HorizontalAlignment="Right" VerticalAlignment="Top" Data="M0,0 L11,0 L11,11" />
    <Path Classes="frame" HorizontalAlignment="Left" VerticalAlignment="Bottom" Data="M0,0 L0,11 L11,11" />
    <Path Classes="frame" HorizontalAlignment="Right" VerticalAlignment="Bottom" Data="M11,0 L11,11 L0,11" />

    <Grid ColumnDefinitions="*,Auto" Margin="20">
      <StackPanel Spacing="10" VerticalAlignment="Center">
        <StackPanel Orientation="Horizontal" Spacing="14">
          <Border Width="48"
                  Height="48"
                  CornerRadius="2"
                  Background="{StaticResource AccentSoftBrush}"
                  VerticalAlignment="Center">
            <Path Classes="icon"
                  Width="24"
                  Height="24"
                  HorizontalAlignment="Center"
                  VerticalAlignment="Center"
                  Data="{Binding FeaturedTool.Tool.CategoryLabel, Converter={x:Static converters:IconGeometryConverter.Instance}}" />
          </Border>
          <StackPanel Spacing="3" VerticalAlignment="Center">
            <TextBlock Text="{Binding FeaturedTool.Tool.Name}"
                       FontSize="16"
                       FontWeight="SemiBold"
                       Foreground="{StaticResource TextPrimaryBrush}" />
            <TextBlock Text="{Binding FeaturedTool.Tool.CategoryDisplay}"
                       FontSize="11"
                       Foreground="{StaticResource TextMutedBrush}" />
          </StackPanel>
        </StackPanel>

        <TextBlock Text="{Binding FeaturedTool.Tool.Description}"
                   FontSize="12"
                   LineHeight="18"
                   Foreground="{StaticResource TextSecondaryBrush}"
                   TextWrapping="Wrap"
                   MaxLines="2"
                   Width="280"
                   HorizontalAlignment="Left" />
      </StackPanel>

      <StackPanel Grid.Column="1"
                  Width="150"
                  Margin="24,0,0,0"
                  Spacing="6"
                  VerticalAlignment="Center">
        <TextBlock Classes="section" Text="上次执行" />
        <TextBlock Classes="num" Text="{Binding FeaturedTool.TimeLabel}" FontSize="12" />
        <TextBlock Text="{Binding FeaturedSummary}"
                   FontSize="11"
                   LineHeight="16"
                   Foreground="{StaticResource TextMutedBrush}"
                   TextWrapping="Wrap"
                   MaxLines="3" />
      </StackPanel>
    </Grid>
  </Panel>
</Button>
```

**Step 2: code-behind**

```csharp
private void OnFeaturedToolClick(object? sender, RoutedEventArgs e)
{
    // 大卡在工具墙上，返回时应回工具墙而不是最近页
    if (sender is Button { Tag: RecentToolViewModel recent } && DataContext is WorkspaceViewModel viewModel)
        viewModel.OpenTool(recent.Tool);
}
```

**Step 3: 构建 + 全量测试**

Run: `dotnet build Sparrow.slnx --nologo -v q`
Expected: 0 errors
Run: `dotnet test tests/Sparrow.Tests --nologo -v q`
Expected: PASS

**Step 4: Commit**

```bash
git add src/Sparrow.App/Views/WorkspaceWindow.axaml src/Sparrow.App/Views/WorkspaceWindow.axaml.cs
git commit -m "feat(workspace): 墙顶最近使用大卡（上次执行时间 + 结果摘要）"
```

---

## Task 7: 文档同步与人工验收

**Files:**
- Modify: `docs/ui-spec.md`（三、工作区窗口）

**Step 1: 更新 ui-spec**

- tab 行：注明「共享层，工具墙与最近页都可见」
- 删除「最近执行」条相关描述
- 补充：墙顶大卡（最近执行工具）、分组折叠、chip 联动展开

**Step 2: 人工验收清单**

Run: `dotnet run --project src/Sparrow.App`
1. 托盘打开工作区 → 切「最近使用」→ 打开工具 → 执行 → 点「‹ 返回」→ 能回到最近页，且 tab 行可见，可切回「工具」
2. 工具台按 Esc → 回到工具墙
3. 工具墙有历史上方出现大卡，显示上次执行时间与摘要
4. 点分类 chip → 该组展开、其余折叠；点组标题可手动折叠/展开
5. 搜索框输入 → 全部组展开且结果可见；清空搜索恢复折叠状态
6. 墙底部不再有「最近执行」条

**Step 3: Commit**

```bash
git add docs/ui-spec.md
git commit -m "docs: 同步工作区布局与导航说明"
```
