using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinDock.Models;
using WinDock.Services;

namespace WinDock.Views;

public sealed class OrganizerWindow : DesktopToolWindow
{
    readonly ListBox fences;
    readonly ListBox items;
    readonly TextBox fileFilter;
    readonly TextBlock sectionTitle, sectionCount, selectionCount;
    readonly StackPanel empty;
    readonly DockPanel selectionBar;
    List<FenceItem> currentItems = [];
    List<FenceInfo> availableFences = [];
    readonly ComboBox destination;
    readonly ComboBox snapshots;
    readonly TextBlock connection;
    PecoFenceClient? client;
    string? executable;
    bool busy, refreshing;
    int selectionVersion;

    public OrganizerWindow(Settings settings) : base(settings, Loc.T("organizerTitle"), 1040, 680)
    {
        MinWidth = 820; MinHeight = 520;
        Layout.Margin = new Thickness(16, 4, 20, 16);
        Layout.RowDefinitions.Add(new RowDefinition());
        Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddHeaderAction(IconButton("\uE72C", "refresh", () => _ = RunAsync(RefreshAsync)));
        var more = IconButton("\uE712", "moreActions", () => { });
        more.Click += (_, _) => ShowMenu(more,
            ("pecoStart", StartPeco), ("pecoLocate", Locate),
            ("fenceShow", () => _ = RunAsync(() => Connected().ShowAsync(true, LifetimeToken))),
            ("fenceHide", () => _ = RunAsync(() => Connected().ShowAsync(false, LifetimeToken))));
        AddHeaderAction(more);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(208) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new Grid { Margin = new Thickness(0, 0, 20, 0) };
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new RowDefinition());
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var sectionLabel = Text(Loc.T("fenceList"), 11, true); sectionLabel.Margin = new Thickness(12, 12, 0, 12);
        sidebar.Children.Add(sectionLabel);
        fences = List();
        ScrollViewer.SetHorizontalScrollBarVisibility(fences, ScrollBarVisibility.Disabled);
        System.Windows.Automation.AutomationProperties.SetName(fences, Loc.T("fenceList"));
        Grid.SetRow(fences, 1); sidebar.Children.Add(fences);
        var create = ActionButton("fenceCreate", () => EditName(false)); create.Margin = new Thickness(8, 16, 8, 8);
        create.Background = Brushes.Transparent; create.BorderThickness = new Thickness(0);
        Grid.SetRow(create, 2); sidebar.Children.Add(create); body.Children.Add(sidebar);
        var main = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            main.RowDefinitions.Add(new RowDefinition { Height = height });
        var heading = new DockPanel { Margin = new Thickness(4, 0, 0, 16) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(ActionButton("fenceSave", () => _ = RunAsync(async () =>
        {
            await Connected().SaveLayoutAsync($"{Loc.T("fenceLayout")} {DateTime.Now:yyyy-MM-dd HH:mm:ss}", LifetimeToken);
            await RefreshSnapshotsAsync();
        })));
        tools.Children.Add(IconButton("\uE81C", "fenceRestore", OpenLayouts));
        var sectionMore = IconButton("\uE712", "moreActions", () => { });
        sectionMore.Click += (_, _) => ShowMenu(sectionMore, ("fenceRename", () => EditName(true)),
            ("fenceOptions", () => _ = RunAsync(async () => { if (SelectedFence is { } f) await Connected().OpenOptionsAsync(f.Id, LifetimeToken); })));
        tools.Children.Add(sectionMore);
        DockPanel.SetDock(tools, Dock.Right); heading.Children.Add(tools);
        var titles = new StackPanel();
        sectionTitle = Text(Loc.T("organizerTitle"), 26); sectionTitle.FontWeight = FontWeights.SemiBold;
        sectionCount = Text("", 11, true); sectionCount.Margin = new Thickness(0, 6, 0, 0);
        titles.Children.Add(sectionTitle); titles.Children.Add(sectionCount); heading.Children.Add(titles); main.Children.Add(heading);
        fileFilter = Input(Loc.T("fenceFilter")); fileFilter.Margin = new Thickness(0, 0, 0, 12);
        Grid.SetRow(fileFilter, 1); main.Children.Add(fileFilter);
        items = List(); items.SelectionMode = SelectionMode.Extended; items.AllowDrop = true;
        ScrollViewer.SetHorizontalScrollBarVisibility(items, ScrollBarVisibility.Disabled);
        System.Windows.Automation.AutomationProperties.SetName(items, Loc.T("fenceItems"));
        var content = new Grid(); content.Children.Add(items);
        empty = EmptyState("\uE8B7", "fenceEmptyTitle", "fenceEmptyHint"); content.Children.Add(empty);
        Grid.SetRow(content, 2); main.Children.Add(content);
        selectionBar = new DockPanel { Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        var move = ActionButton("fenceMove", () => _ = RunAsync(MoveSelectedAsync));
        DockPanel.SetDock(move, Dock.Right); selectionBar.Children.Add(move);
        destination = new ComboBox { Width = 185, Margin = new Thickness(8, 0, 8, 0) };
        System.Windows.Automation.AutomationProperties.SetName(destination, Loc.T("fenceMoveTo"));
        DockPanel.SetDock(destination, Dock.Right); selectionBar.Children.Add(destination);
        selectionCount = Text("", 12, true); selectionBar.Children.Add(selectionCount);
        Grid.SetRow(selectionBar, 3); main.Children.Add(selectionBar);
        Grid.SetColumn(main, 1); body.Children.Add(main); Layout.Children.Add(body);
        var footer = new DockPanel { Margin = new Thickness(12, 14, 0, 0) };
        connection = Text("PecoFence", 11, true); connection.Margin = new Thickness(16, 0, 0, 0);
        DockPanel.SetDock(connection, Dock.Right); footer.Children.Add(connection); footer.Children.Add(Status);
        Grid.SetRow(footer, 1); Layout.Children.Add(footer);
        snapshots = new ComboBox { MinWidth = 320, Margin = new Thickness(0, 16, 0, 20) };
        System.Windows.Automation.AutomationProperties.SetName(snapshots, Loc.T("fenceLayout"));
        fences.SelectionChanged += async (_, _) => { if (!refreshing) await LoadSelectionAsync(); };
        fileFilter.TextChanged += (_, _) => RenderItems();
        items.SelectionChanged += (_, _) =>
        {
            selectionBar.Visibility = items.SelectedItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            selectionCount.Text = Loc.F("fenceSelected", items.SelectedItems.Count);
        };
        items.MouseDoubleClick += (_, _) => { if ((items.SelectedItem as ListBoxItem)?.Tag is FenceItem item) Launcher.OpenFile(item.Path); };
        items.PreviewDragOver += (_, e) =>
        {
            e.Effects = !busy && SelectedFence is { IsVirtual: true } && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
        };
        items.Drop += async (_, e) =>
        {
            e.Handled = true;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || SelectedFence is not { IsVirtual: true } target) return;
            await RunAsync(async () =>
            {
                var pathsSet = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var selected = (await Connected().AllItemsAsync(LifetimeToken)).Where(i => pathsSet.Contains(i.Path)).ToList();
                if (selected.Count != pathsSet.Count) throw new PecoFenceException(Loc.T("pecoDesktopOnly"));
                await Connected().MoveAsync(selected, target.Id, LifetimeToken); await RefreshAsync();
            });
        };
        Loaded += async (_, _) => await RunAsync(RefreshAsync);
    }

    void ShowMenu(Button anchor, params (string Key, Action Action)[] actions)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Resources = Resources };
        foreach (var (key, action) in actions)
        {
            var item = new MenuItem { Header = Loc.T(key) }; item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        PopupInteraction.Open(menu, anchor);
    }

    void EditName(bool rename)
    {
        var selected = SelectedFence;
        if (rename && selected is null) return;
        var dialog = new ToolDialog(Settings, Loc.T(rename ? "fenceRename" : "fenceCreate")) { Owner = this };
        var input = Input(Loc.T("fenceName")); input.Text = rename ? selected!.Title : "";
        input.Margin = new Thickness(0, 12, 0, 20); dialog.Body.Children.Add(input);
        dialog.Body.Children.Add(ActionButton("save", () =>
        {
            var value = input.Text.Trim();
            if (value.Length is < 1 or > 100 || value.Any(char.IsControl)) { dialog.Message.Text = Loc.T("pecoNameRequired"); return; }
            dialog.Close();
            _ = RunAsync(async () =>
            {
                if (rename) await Connected().RenameAsync(selected!.Id, value, LifetimeToken);
                else await Connected().CreateAsync(value, LifetimeToken);
                await RefreshAsync();
            });
        }));
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        dialog.ShowDialog();
    }

    void OpenLayouts()
    {
        var dialog = new ToolDialog(Settings, Loc.T("fenceRestore")) { Owner = this };
        dialog.Body.Children.Add(snapshots);
        dialog.Body.Children.Add(ActionButton("fenceRestore", () => { dialog.Close(); Restore(); }));
        dialog.Closed += (_, _) => dialog.Body.Children.Remove(snapshots);
        dialog.ShowDialog();
    }

    void RenderItems()
    {
        items.Items.Clear();
        var filtered = currentItems.Where(i => i.FileName.Contains(fileFilter.Text.Trim(), StringComparison.CurrentCultureIgnoreCase));
        foreach (var item in filtered) items.Items.Add(Row(item, item.FileName, item.Path, item.IsFolder ? "\uE8B7" : "\uE8A5"));
        empty.Visibility = items.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ((TextBlock)empty.Children[1]).Text = Loc.T(fileFilter.Text.Length > 0 ? "searchNoResults" : "fenceEmptyTitle");
        ((TextBlock)empty.Children[2]).Text = Loc.T(fileFilter.Text.Length > 0 ? "searchTryAnother" : "fenceEmptyHint");
    }

    FenceInfo? SelectedFence => (fences.SelectedItem as ListBoxItem)?.Tag as FenceInfo;
    PecoFenceClient Connected() => client ?? throw new PecoFenceException(Loc.T("pecoUnavailable"));

    async Task RunAsync(Func<Task> operation)
    {
        if (busy) return;
        busy = true; Status.Text = Loc.T("working");
        try
        {
            await operation();
            if (!Lifetime.IsCancellationRequested) Status.Text = Loc.T("pecoReady");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!Lifetime.IsCancellationRequested) Status.Text = e.Message; }
        finally { busy = false; }
    }

    async Task RefreshAsync()
    {
        executable = PecoFenceClient.FindExecutable(Settings.Desktop.PecoFencePath);
        if (executable is null) { client = null; throw new PecoFenceException(Loc.T("pecoUnavailable")); }
        client = new PecoFenceClient(new PecoProcessRunner(executable));
        var list = await client.FencesAsync(LifetimeToken);
        var previous = SelectedFence?.Id;
        refreshing = true;
        try
        {
            fences.Items.Clear();
            foreach (var fence in list) fences.Items.Add(Row(fence, fence.Title, Loc.F("fenceItemCount", fence.ItemCount) + (fence.TabHost is not null ? " · " + Loc.T("fenceTab") : "") + (!fence.IsVirtual ? " · " + Loc.T("fencePortal") : "")));
            availableFences = list;
            fences.SelectedIndex = Math.Max(0, list.FindIndex(f => f.Id == previous));
            connection.Text = "PecoFence";
        }
        finally { refreshing = false; }
        await LoadSelectionAsync();
        await RefreshSnapshotsAsync();
    }

    async Task LoadSelectionAsync()
    {
        var current = ++selectionVersion;
        if (SelectedFence is not { } selected || client is null) return;
        sectionTitle.Text = selected.Title;
        destination.ItemsSource = availableFences.Where(f => f.IsVirtual && f.Id != selected.Id).ToList();
        destination.SelectedIndex = destination.Items.Count > 0 ? 0 : -1;
        currentItems = []; RenderItems(); empty.Visibility = Visibility.Collapsed;
        try
        {
            var list = await client.ItemsAsync(selected.Id, LifetimeToken);
            if (current != selectionVersion || Lifetime.IsCancellationRequested) return;
            currentItems = list;
            sectionCount.Text = Loc.F("fenceItemCount", list.Count);
            RenderItems();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (current == selectionVersion) Status.Text = e.Message; }
    }

    async Task RefreshSnapshotsAsync()
    {
        snapshots.ItemsSource = (await Connected().SnapshotsAsync(LifetimeToken)).OrderByDescending(s => s.Ts).ToList();
        if (snapshots.Items.Count > 0) snapshots.SelectedIndex = 0;
    }

    async Task MoveSelectedAsync()
    {
        if (destination.SelectedItem is not FenceInfo target) return;
        var selected = items.SelectedItems.Cast<ListBoxItem>().Select(i => (FenceItem)i.Tag).ToArray();
        if (selected.Length == 0) throw new PecoFenceException(Loc.T("pecoSelectItems"));
        await Connected().MoveAsync(selected, target.Id, LifetimeToken);
        await RefreshAsync();
    }

    void Restore()
    {
        if (snapshots.SelectedItem is not FenceSnapshot snapshot) return;
        if (MessageBox.Show(this, Loc.F("fenceRestoreAsk", snapshot.Name), Loc.T("organizerTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _ = RunAsync(async () => { await Connected().RestoreAsync(snapshot.Id, LifetimeToken); await RefreshAsync(); });
    }

    void Locate()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("pecoLocate"), Filter = "PecoFence|pecofence-cli.exe;pecofence.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        Settings.Desktop.PecoFencePath = picker.FileName;
        Settings.Save();
        _ = RunAsync(RefreshAsync);
    }

    void StartPeco()
    {
        executable = PecoFenceClient.FindExecutable(Settings.Desktop.PecoFencePath);
        if (executable is null) { Status.Text = Loc.T("pecoUnavailable"); return; }
        var app = Path.Combine(Path.GetDirectoryName(executable)!, "pecofence.exe");
        if (!Launcher.OpenFile(app)) { Status.Text = Loc.T("pecoUnavailable"); return; }
        _ = RunAsync(async () => { await Task.Delay(1400, LifetimeToken); await RefreshAsync(); });
    }
}
