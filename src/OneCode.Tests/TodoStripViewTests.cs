using OneCode.App.Tui;
using OneCode.Core.Coordinator;

namespace OneCode.Tests;

/// <summary>
/// 待办横条的行数契约：RequiredRows 决定 <c>ReplShell.ApplyTodoStripLayout</c>
/// 从内容区借走多少行——算错会挤压对话区或截断清单。
/// </summary>
public sealed class TodoStripViewTests
{
    [Fact]
    public void RequiredRows_EmptySnapshot_IsZero()
    {
        var view = new TodoStripView();

        view.Update([]);

        view.RequiredRows.Should().Be(0, "空清单必须整体隐藏，不占对话区高度");
    }

    [Fact]
    public void RequiredRows_SingleItem_HeaderPlusOneRow()
    {
        var view = new TodoStripView();

        view.Update([Item(1, "only")]);

        view.RequiredRows.Should().Be(2);
    }

    [Fact]
    public void RequiredRows_AtCap_HeaderPlusFourItemsNoOverflow()
    {
        var view = new TodoStripView();

        view.Update(Items(TodoStripView.MaxItems));

        view.RequiredRows.Should().Be(TodoStripView.MaxItems + 1);
    }

    [Fact]
    public void RequiredRows_OverCap_AddsSingleOverflowRow()
    {
        var view = new TodoStripView();

        view.Update(Items(TodoStripView.MaxItems + 1));

        view.RequiredRows.Should().Be(TodoStripView.MaxItems + 2,
            "超出封顶行数只追加一行汇总，不得逐条展开继续借对话区高度");
    }

    private static TodoListItem Item(int id, string title, bool isComplete = false)
        => new(id, title, null, isComplete);

    private static IReadOnlyList<TodoListItem> Items(int count)
        => Enumerable.Range(1, count).Select(i => Item(i, $"item {i}")).ToList();
}
