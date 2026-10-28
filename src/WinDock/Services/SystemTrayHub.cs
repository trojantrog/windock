using WinDock.ViewModels;
using WinDock.Views;

namespace WinDock.Services;

public sealed class SystemTrayHub : IDisposable
{
    public static SystemTrayHub? Current { get; private set; }
    public SystemTrayService Service { get; } = new();
    public DockOptions Options { get; }
    public event Action? PresentationChanged;
    SystemTrayWindow? strip;
    public SystemTrayHub(DockOptions options)
    {
        Options = options; Current = this;
        options.Changed += Changed; Apply();
    }
    void Changed(string name)
    {
        if (name is nameof(DockOptions.SystemTrayEnabled) or nameof(DockOptions.TrayPinned) or nameof(DockOptions.Enabled)) Apply();
        if (name is nameof(DockOptions.TrayIconSize) or nameof(DockOptions.TrayColumns) or nameof(DockOptions.TrayShowNames)
            or nameof(DockOptions.TraySort) or nameof(DockOptions.TrayPinnedIds) or nameof(DockOptions.TrayOrder) or nameof(DockOptions.Theme))
            PresentationChanged?.Invoke();
    }
    void Apply()
    {
        if (Options.SystemTrayEnabled && Options.Enabled) Service.Start(); else Service.Stop();
        if (Options.SystemTrayEnabled && Options.TrayPinned && Options.Enabled)
        {
            strip ??= new SystemTrayWindow(this);
            if (!strip.IsVisible) strip.Show();
        }
        else { strip?.Close(); strip = null; }
    }
    public void Retry() { Service.Stop(); if (Options.SystemTrayEnabled && Options.Enabled) Service.Start(); }
    public bool IsPinned(SystemTrayItem item) => item.StableId.Length > 0 && Options.TrayPinnedIds.Contains(item.StableId, StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<SystemTrayItem> OrderedIcons()
    {
        var visible = Service.Icons.Where(icon => !icon.Hidden);
        if (Options.TraySort == "name") return visible.OrderBy(icon => icon.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        var order = Options.TrayOrder.Select((id, index) => (id, index))
            .GroupBy(value => value.id, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);
        return visible.OrderBy(icon => order.GetValueOrDefault(icon.StableId, int.MaxValue)).ToList();
    }
    public void Move(SystemTrayItem item, string? beforeId)
    {
        if (item.StableId.Length == 0 || item.StableId.Equals(beforeId, StringComparison.OrdinalIgnoreCase)) return;
        var ids = OrderedIcons().Select(icon => icon.StableId).Where(id => id.Length > 0)
            .Concat(Options.TrayOrder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ids.RemoveAll(id => id.Equals(item.StableId, StringComparison.OrdinalIgnoreCase));
        var index = beforeId is null ? -1 : ids.FindIndex(id => id.Equals(beforeId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) ids.Add(item.StableId); else ids.Insert(index, item.StableId);
        Options.SetTrayOrder(ids);
        if (Options.TraySort != "system") Options.TraySort = "system";
    }
    public void Dispose()
    {
        Options.Changed -= Changed; strip?.Close(); strip = null; Service.Dispose();
        if (Current == this) Current = null;
    }
}
