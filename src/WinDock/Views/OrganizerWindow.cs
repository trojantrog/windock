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
    readonly Button move;
    readonly List<Control> commands = [], connectedCommands = [];
    PecoFenceClient? client;
    string? executable;
    string? selectionError;
    CancellationTokenSource? selectionRequest;
    bool busy, refreshing, loading;
    int selectionVersion;

    public OrganizerWindow(Settings settings) : base(settings, Loc.T("organizerTitle"), 1040, 680)
    {
        MinWidth = 820; MinHeight = 520;
        Layout.Margin = new Thickness(16, 4, 20, 16);
        Layout.RowDefinitions.Add(new RowDefinition());
        Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var refresh = IconButton("\uE72C", "refresh", () => _ = RunAsync(RefreshAsync));
        commands.Add(refresh); AddHeaderAction(refresh);
        var more = IconButton("\uE712", "moreActions", () => { });
        more.Click += (_, _) => ShowMenu(more,
            ("pecoStart", StartPeco), ("pecoLocate", Locate),
            ("fenceShow", () => _ = RunAsync(() => Connected().ShowAsync(true, LifetimeToken))),
            ("fenceHide", () => _ = RunAsync(() => Connected().ShowAsync(false, LifetimeToken))));
        commands.Add(more); AddHeaderAction(more);
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
        connectedCommands.Add(create);
        create.Background = Brushes.Transparent; create.BorderThickness = new Thickness(0);
        Grid.SetRow(create, 2); sidebar.Children.Add(create); body.Children.Add(sidebar);
        var main = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            main.RowDefinitions.Add(new RowDefinition { Height = height });
        var heading = new DockPanel { Margin = new Thickness(4, 0, 0, 16) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var save = ActionButton("fenceSave", () => _ = RunAsync(async () =>
        {
            await Connected().SaveLayoutAsync($"{Loc.T("fenceLayout")} {DateTime.Now:yyyy-MM-dd HH:mm:ss}", LifetimeToken);
            await RefreshSnapshotsAsync(selectLatest: true);
            Status.Text = Loc.T("fenceLayoutSaved");
        }));
        connectedCommands.Add(save); tools.Children.Add(save);
        var restore = IconButton("\uE81C", "fenceRestore", OpenLayouts);
        connectedCommands.Add(restore); tools.Children.Add(restore);
        var sectionMore = IconButton("\uE712", "moreActions", () => { });
        sectionMore.Click += (_, _) => ShowMenu(sectionMore, ("fenceRename", () => EditName(true)),
            ("fenceOptions", () => _ = RunAsync(async () => { if (SelectedFence is { } f) await Connected().OpenOptionsAsync(f.Id, LifetimeToken); })));
        connectedCommands.Add(sectionMore); tools.Children.Add(sectionMore);
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
        move = ActionButton("fenceMove", () => _ = RunAsync(MoveSelectedAsync)); move.IsEnabled = false;
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
        items.SelectionChanged += (_, _) => UpdateInteraction();
        destination.SelectionChanged += (_, _) => UpdateInteraction();
        items.MouseDoubleClick += (_, _) =>
        {
            if ((items.SelectedItem as ListBoxItem)?.Tag is FenceItem item && !Launcher.OpenFile(item.Path))
                Status.Text = Loc.F("launchFailed", item.FileName);
        };
        items.PreviewDragOver += (_, e) =>
        {
            e.Effects = !busy && !loading && client is not null && SelectedFence is { IsVirtual: true } && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
        };
        items.Drop += async (_, e) =>
        {
            e.Handled = true;
            if (busy || loading || client is null || e.Data.GetData(DataFormats.FileDrop) is not string[] paths || SelectedFence is not { IsVirtual: true } target) return;
            await RunAsync(async () =>
            {
                var pathsSet = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var selected = (await Connected().AllItemsAsync(LifetimeToken)).Where(i => pathsSet.Contains(i.Path)).ToList();
                if (selected.Count != pathsSet.Count) throw new PecoFenceException(Loc.T("pecoDesktopOnly"));
                await Connected().MoveAsync(selected, target.Id, LifetimeToken); await RefreshAsync();
            });
        };
        Loaded += async (_, _) => await RunAsync(RefreshAsync);
        Closed += (_, _) => CancelSelection();
        UpdateInteraction();
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
        if (busy || client is null) return;
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
        if (busy || client is null) return;
        var dialog = new ToolDialog(Settings, Loc.T("fenceRestore")) { Owner = this };
        dialog.Body.Children.Add(snapshots);
        var restore = ActionButton("fenceRestore", () => { dialog.Close(); Restore(); });
        restore.IsEnabled = snapshots.Items.Count > 0; dialog.Body.Children.Add(restore);
        dialog.Closed += (_, _) => dialog.Body.Children.Remove(snapshots);
        dialog.ShowDialog();
    }

    void RenderItems()
    {
        var selected = items.SelectedItems.Cast<ListBoxItem>().Select(row => ((FenceItem)row.Tag).Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        items.Items.Clear();
        var filtered = currentItems.Where(i => i.FileName.Contains(fileFilter.Text.Trim(), StringComparison.CurrentCultureIgnoreCase));
        foreach (var item in filtered)
        {
            var visual = SearchVisual.For(new SearchHit(item.FileName, item.Path, item.IsFolder ? "folder" : "file", item.Path));
            var row = Row(item, item.FileName, item.Path, visual.Glyph);
            ((TextBlock)((Grid)row.Content).Children[0]).Foreground = SystemParameters.HighContrast ? Ink : Paint(visual.Color);
            items.Items.Add(row);
            if (selected.Contains(item.Id)) row.IsSelected = true;
        }
        empty.Visibility = items.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ((TextBlock)empty.Children[1]).Text = Loc.T(loading ? "fenceLoading" : selectionError is not null ? "fenceLoadFailed"
            : fileFilter.Text.Trim().Length > 0 ? "searchNoResults" : "fenceEmptyTitle");
        ((TextBlock)empty.Children[2]).Text = loading ? "" : selectionError ?? Loc.T(fileFilter.Text.Trim().Length > 0 ? "searchTryAnother" : "fenceEmptyHint");
        UpdateInteraction();
    }

    void UpdateInteraction()
    {
        foreach (var command in commands) command.IsEnabled = !busy;
        foreach (var command in connectedCommands) command.IsEnabled = !busy && client is not null;
        fences.IsEnabled = !busy && client is not null;
        items.IsEnabled = !busy && !loading && client is not null && selectionError is null;
        fileFilter.IsEnabled = !busy && client is not null;
        destination.IsEnabled = !busy && !loading;
        var canMove = SelectedFence is { IsVirtual: true } && items.SelectedItems.Count > 0;
        selectionBar.Visibility = canMove ? Visibility.Visible : Visibility.Collapsed;
        selectionCount.Text = Loc.F("fenceSelected", items.SelectedItems.Count);
        move.IsEnabled = !busy && !loading && canMove && destination.SelectedItem is FenceInfo;
    }

    FenceInfo? SelectedFence => (fences.SelectedItem as ListBoxItem)?.Tag as FenceInfo;
    PecoFenceClient Connected() => client ?? throw new PecoFenceException(Loc.T("pecoUnavailable"));

    async Task RunAsync(Func<Task> operation)
    {
        if (busy) return;
        busy = true; Status.Text = Loc.T("working");
        UpdateInteraction();
        try
        {
            await operation();
            if (!LifetimeToken.IsCancellationRequested && Status.Text == Loc.T("working")) Status.Text = Loc.T("pecoReady");
        }
        catch (OperationCanceledException) { }
        catch (PecoFenceException e) when (e.MayHaveCompleted && !LifetimeToken.IsCancellationRequested)
        {
            // Reconcile read-only state after an uncertain write; never repeat the write.
            try { await RefreshAsync(); }
            catch (Exception) { }
            if (!LifetimeToken.IsCancellationRequested) Status.Text = e.Message;
        }
        catch (Exception e) { if (!LifetimeToken.IsCancellationRequested) Status.Text = e.Message; }
        finally { busy = false; if (!LifetimeToken.IsCancellationRequested) UpdateInteraction(); }
    }

    async Task RefreshAsync()
    {
        executable = PecoFenceClient.FindExecutable(Settings.Desktop.PecoFencePath);
        if (executable is null) { ClearConnection(Loc.T("pecoUnavailable")); throw new PecoFenceException(Loc.T("pecoUnavailable")); }
        var next = new PecoFenceClient(new PecoProcessRunner(executable));
        List<FenceInfo> list;
        try { list = await next.FencesAsync(LifetimeToken); }
        catch (OperationCanceledException) when (LifetimeToken.IsCancellationRequested) { throw; }
        catch (Exception e) { ClearConnection(e.Message); throw; }
        client = next;
        var previous = SelectedFence?.Id;
        refreshing = true;
        try
        {
            fences.Items.Clear();
            foreach (var fence in list) fences.Items.Add(Row(fence, fence.Title, Loc.F("fenceItemCount", fence.ItemCount) + (fence.TabHost is not null ? " · " + Loc.T("fenceTab") : "") + (!fence.IsVirtual ? " · " + Loc.T("fencePortal") : "")));
            availableFences = list;
            fences.SelectedIndex = list.Count == 0 ? -1 : Math.Max(0, list.FindIndex(f => f.Id == previous));
            connection.Text = "PecoFence";
        }
        finally { refreshing = false; }
        await LoadSelectionAsync();
        await RefreshSnapshotsAsync();
    }

    async Task LoadSelectionAsync()
    {
        CancelSelection();
        var current = selectionVersion;
        selectionError = null; loading = false;
        currentItems = []; sectionCount.Text = ""; destination.ItemsSource = null;
        if (SelectedFence is not { } selected || client is null)
        {
            sectionTitle.Text = Loc.T("organizerTitle");
            RenderItems(); return;
        }
        sectionTitle.Text = selected.Title;
        destination.ItemsSource = availableFences.Where(f => f.IsVirtual && f.Id != selected.Id).ToList();
        destination.SelectedIndex = destination.Items.Count > 0 ? 0 : -1;
        var request = CancellationTokenSource.CreateLinkedTokenSource(LifetimeToken);
        selectionRequest = request;
        loading = true; RenderItems();
        try
        {
            var list = await client.ItemsAsync(selected.Id, request.Token);
            if (current != selectionVersion || request.IsCancellationRequested) return;
            currentItems = list;
            sectionCount.Text = Loc.F("fenceItemCount", list.Count);
            if (!busy) Status.Text = Loc.T("pecoReady");
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (current == selectionVersion && !request.IsCancellationRequested)
                selectionError = Status.Text = e.Message;
        }
        finally
        {
            if (ReferenceEquals(selectionRequest, request)) selectionRequest = null;
            request.Dispose();
            if (current == selectionVersion && !LifetimeToken.IsCancellationRequested)
            {
                loading = false; RenderItems();
            }
        }
    }

    void CancelSelection()
    {
        ++selectionVersion;
        selectionRequest?.Cancel();
        selectionRequest = null;
    }

    void ClearConnection(string error)
    {
        CancelSelection();
        client = null;
        refreshing = true;
        try { fences.Items.Clear(); availableFences = []; }
        finally { refreshing = false; }
        loading = false; selectionError = error;
        currentItems = []; destination.ItemsSource = null; snapshots.ItemsSource = null;
        sectionTitle.Text = Loc.T("organizerTitle"); sectionCount.Text = "";
        RenderItems();
    }

    async Task RefreshSnapshotsAsync(bool selectLatest = false)
    {
        var previous = (snapshots.SelectedItem as FenceSnapshot)?.Id;
        var layouts = (await Connected().SnapshotsAsync(LifetimeToken)).OrderByDescending(s => s.Ts).ToList();
        snapshots.ItemsSource = layouts;
        if (layouts.Count > 0) snapshots.SelectedIndex = selectLatest ? 0
            : Math.Max(0, layouts.FindIndex(layout => string.Equals(layout.Id, previous, StringComparison.OrdinalIgnoreCase)));
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
        if (busy) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("pecoLocate"), Filter = "PecoFence|pecofence-cli.exe;pecofence.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        Settings.Desktop.PecoFencePath = picker.FileName;
        Settings.Save();
        _ = RunAsync(RefreshAsync);
    }

    void StartPeco()
    {
        if (busy) return;
        executable = PecoFenceClient.FindExecutable(Settings.Desktop.PecoFencePath);
        if (executable is null) { Status.Text = Loc.T("pecoUnavailable"); return; }
        var app = Path.Combine(Path.GetDirectoryName(executable)!, "pecofence.exe");
        if (!Launcher.OpenFile(app)) { Status.Text = Loc.T("pecoUnavailable"); return; }
        _ = RunAsync(async () => { await Task.Delay(1400, LifetimeToken); await RefreshAsync(); });
    }
}
