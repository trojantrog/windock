using WinDock.Services;
using WinDock.Views;

namespace WinDock.Tests;

public class SearchFilterTests
{
    [Fact]
    public void Folder_scope_is_delimited_and_does_not_include_a_sibling_with_the_same_prefix()
    {
        var filter = new SearchFilter(SearchCategory.Documents, @"D:\课程 资料");
        Assert.True(filter.Accepts(new("a.PDF", "", "file", @"D:\课程 资料\a.PDF")));
        Assert.False(filter.Accepts(new("a.pdf", "", "file", @"D:\课程 资料副本\a.pdf")));
        Assert.False(filter.Accepts(new("a.pdf", "", "folder", @"D:\课程 资料\a.pdf")));
        Assert.False(filter.Accepts(new("a.png", "", "file", @"D:\课程 资料\a.png")));
    }

    [Fact]
    public void Categories_and_paths_are_part_of_the_query_before_the_result_limit()
    {
        var query = new SearchFilter(SearchCategory.Documents, @"D:\课程 资料").Query("设计|报告");
        Assert.StartsWith("\"D:\\课程 资料\\\" file: ext:", query);
        Assert.EndsWith("<设计|报告>", query);
        Assert.Contains("docx", query);
        Assert.Equal("folder: <test>", new SearchFilter(SearchCategory.Folders).Query("test"));
    }

    [Fact]
    public void Application_search_uses_the_catalog_and_ignores_file_scope()
    {
        var filter = new SearchFilter(SearchCategory.Applications, @"D:\课程");
        Assert.True(filter.IncludeApplications); Assert.Null(filter.Root);
        Assert.True(filter.Accepts(new("Edge", "", "app", "")));
        Assert.False(filter.Accepts(new("Edge.exe", "", "file", @"D:\课程\Edge.exe")));
        Assert.False(new SearchFilter(SearchCategory.All, @"D:\课程").IncludeApplications);
    }

    [Theory]
    [InlineData("report.PDF", "PDF")]
    [InlineData("report.docx", "Word")]
    public void Document_formats_have_distinct_result_identity(string name, string label)
    {
        Assert.Equal(label, SearchVisual.For(new(name, "", "file", name)).Label);
        Assert.NotEqual(SearchVisual.For(new("a.pdf", "", "file", "a.pdf")), SearchVisual.For(new("a.xlsx", "", "file", "a.xlsx")));
    }

    [Fact]
    public void Pointer_motion_settles_without_overshooting_and_is_frame_rate_independent()
    {
        var a = new PointerSpring(0); var b = a;
        for (var i = 0; i < 30; i++) { a = a.Step(100, 1.0 / 60); Assert.InRange(a.Position, 0, 100); }
        for (var i = 0; i < 72; i++) b = b.Step(100, 1.0 / 144);
        Assert.Equal(a.Position, b.Position, 8); Assert.True(a.Settled(100));
        var interrupted = a.Step(-100, 1.0 / 60); Assert.True(double.IsFinite(interrupted.Position));
    }
}
