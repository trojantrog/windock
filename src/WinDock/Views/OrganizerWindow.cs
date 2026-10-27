using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinDock.Models;
using WinDock.Services;

namespace WinDock.Views;

public sealed class OrganizerWindow : DesktopToolWindow
{
    readonly ListBox fences;
    readonly ListBox items;
    readonly TextBox title;
    readonly ComboBox destination;
    readonly ComboBox snapshots;
    readonly TextBlock connection;
    PecoFenceClient? client;
    string? executable;
    bool busy, refreshing;
    int selectionVersion;

    public OrganizerWindow(Settings settings) : base(settings, Loc.T("organizerTitle"), 1040, 740)
    {
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            Layout.RowDefinitions.Add(new RowDefinition { Height = height });
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var top = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        top.Children.Add(ActionButton("pecoStart", StartPeco));
        top.Children.Add(ActionButton("pecoLocate", Locate));
        top.Children.Add(ActionButton("refresh", () => _ = RunAsync(RefreshAsync)));
        DockPanel.SetDock(top, Dock.Right); heading.Children.Add(top);
        heading.Children.Add(Text(Loc.T("organizerTitle"), 24)); Layout.Children.Add(heading);
        connection = Text(Loc.T("pecoHint"), 12, true);
        connection.TextWrapping = TextWrapping.Wrap;
        connection.Margin = new Thickness(0, 0, 0, 14);
        Grid.SetRow(connection, 1); Layout.Children.Add(connection);

        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) }); body.ColumnDefinitions.Add(new ColumnDefinition());
        fences = List(); fences.Margin = new Thickness(0, 0, 12, 0);
        System.Windows.Automation.AutomationProperties.SetName(fences, Loc.T("fenceList"));
        body.Children.Add(fences);
        items = List(); items.SelectionMode = SelectionMode.Extended; items.AllowDrop = true;
        System.Windows.Automation.AutomationProperties.SetName(items, Loc.T("fenceItems"));
        Grid.SetColumn(items, 1); body.Children.Add(items);
        Grid.SetRow(body, 2); Layout.Children.Add(body);

        var controls = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        var names = new WrapPanel();
        title = Input(Loc.T("fenceName"), 180);
        System.Windows.Automation.AutomationProperties.SetName(title, Loc.T("fenceName"));
        names.Children.Add(title);
        names.Children.Add(ActionButton("fenceCreate", () => _ = RunAsync(async () => { await Connected().CreateAsync(title.Text, LifetimeToken); await RefreshAsync(); })));
        names.Children.Add(ActionButton("fenceRename", () => _ = RunAsync(async () => { if (SelectedFence is { } f) { await Connected().RenameAsync(f.Id, title.Text, LifetimeToken); await RefreshAsync(); } })));
        names.Children.Add(ActionButton("fenceOptions", () => _ = RunAsync(async () => { if (SelectedFence is { } f) await Connected().OpenOptionsAsync(f.Id, LifetimeToken); })));
        names.Children.Add(ActionButton("fenceShow", () => _ = RunAsync(async () => { await Connected().ShowAsync(true, LifetimeToken); })));
        names.Children.Add(ActionButton("fenceHide", () => _ = RunAsync(async () => { await Connected().ShowAsync(false, LifetimeToken); })));
        controls.Children.Add(names);

        var moves = new WrapPanel();
        moves.Children.Add(Text(Loc.T("fenceMoveTo")));
        destination = new ComboBox { MinWidth = 180, Margin = new Thickness(10, 0, 8, 6), Padding = new Thickness(6) };
        System.Windows.Automation.AutomationProperties.SetName(destination, Loc.T("fenceMoveTo"));
        moves.Children.Add(destination);
        moves.Children.Add(ActionButton("fenceMove", () => _ = RunAsync(MoveSelectedAsync)));
        moves.Children.Add(ActionButton("fenceSave", () => _ = RunAsync(async () =>
        {
            await Connected().SaveLayoutAsync($"{Loc.T("fenceLayout")} {DateTime.Now:yyyy-MM-dd HH:mm:ss}", LifetimeToken); await RefreshSnapshotsAsync();
        })));
        snapshots = new ComboBox { MinWidth = 190, MaxWidth = 260, Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(6) };
        System.Windows.Automation.AutomationProperties.SetName(snapshots, Loc.T("fenceLayout"));
        moves.Children.Add(snapshots);
        moves.Children.Add(ActionButton("fenceRestore", Restore));
        controls.Children.Add(moves);
        Grid.SetRow(controls, 3); Layout.Children.Add(controls);
        Grid.SetRow(Status, 4); Layout.Children.Add(Status);

        fences.SelectionChanged += async (_, _) => { if (!refreshing) await LoadSelectionAsync(); };
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
                await Connected().MoveAsync(selected, target.Id, LifetimeToken);
                await RefreshAsync();
            });
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += async (_, _) => await RunAsync(RefreshAsync);
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
            destination.ItemsSource = list.Where(f => f.IsVirtual).ToList();
            destination.SelectedIndex = destination.Items.Count > 0 ? 0 : -1;
            fences.SelectedIndex = Math.Max(0, list.FindIndex(f => f.Id == previous));
            connection.Text = Loc.T("pecoHint");
        }
        finally { refreshing = false; }
        await LoadSelectionAsync();
        await RefreshSnapshotsAsync();
    }

    async Task LoadSelectionAsync()
    {
        var current = ++selectionVersion;
        if (SelectedFence is not { } selected || client is null) return;
        title.Text = selected.Title;
        try
        {
            var list = await client.ItemsAsync(selected.Id, LifetimeToken);
            if (current != selectionVersion || Lifetime.IsCancellationRequested) return;
            items.Items.Clear();
            foreach (var item in list) items.Items.Add(Row(item, item.FileName, item.Path));
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
