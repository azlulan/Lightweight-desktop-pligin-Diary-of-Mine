using System;

namespace DiaryOfMine.Desktop.Navigation;

public sealed class NavigationPageRegistration
{
    private readonly Func<object> _pageFactory;

    public string Id { get; }
    public string Title { get; }
    public string Glyph { get; }
    public int Order { get; }
    public NavigationPlacement Placement { get; }

    public NavigationPageRegistration(
        string id,
        string title,
        string glyph,
        int order,
        NavigationPlacement placement,
        Func<object> pageFactory)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("导航标识不能为空。", nameof(id));
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("导航标题不能为空。", nameof(title));

        Id = id.Trim();
        Title = title.Trim();
        Glyph = glyph ?? string.Empty;
        Order = order;
        Placement = placement;
        _pageFactory = pageFactory ?? throw new ArgumentNullException(nameof(pageFactory));
    }

    internal object CreatePage() => _pageFactory()
        ?? throw new InvalidOperationException($"导航页面“{Id}”的工厂返回了空值。");
}
