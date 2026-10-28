using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinDock.Services;

sealed class PopupInteraction : IDisposable
{
    static readonly Dictionary<Window, PopupInteraction> instances = [];
    static readonly Dictionary<ContextMenu, Window?> menus = [];
    static readonly Dictionary<ContextMenu, nint> menuWindows = [];
    static readonly MouseHookProc mouseProc = MouseEvent;
    static readonly Native.WinEventProc foregroundProc = ForegroundEvent, menuProc = MenuEvent;
    static nint mouseHook, foregroundHook, menuHook;
    static long inputSequence;
    static uint inputTime;
    static (WeakReference<FrameworkElement> Anchor, long Sequence)? dismissedAnchor;
    readonly Window window;
    readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromMilliseconds(80) };
    Action? dismiss;
    Func<bool>? keepOpen;
    TrayMenuSession? external;
    bool disposed, reviewQueued;
    long generation;
    nint acceptedForeground;
    bool acceptInactive;
    (MouseButton Button, long Sequence)? consumedRelease;
    public FrameworkElement? Anchor { get; set; }
    public event Action? Changed;
    public event Action<SystemTrayItem>? TrayFocusRestored;

    static PopupInteraction()
    {
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(MenuOpened));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler(MenuClosed));
        EventManager.RegisterClassHandler(typeof(ContextMenu), Keyboard.PreviewKeyDownEvent, new KeyEventHandler(MenuKeyDown), true);
    }

    PopupInteraction(Window window)
    {
        this.window = window;
        window.Deactivated += Deactivated;
        window.Activated += Activated;
        window.LostMouseCapture += CaptureChanged;
        window.PreviewMouseDown += PointerDown;
        window.PreviewMouseUp += PointerUp;
        window.PreviewKeyDown += KeyDown;
        window.IsVisibleChanged += VisibilityChanged;
        window.Closed += Closed;
        poll.Tick += Tick;
    }

    public static PopupInteraction For(Window window)
    {
        if (!instances.TryGetValue(window, out var value)) instances[window] = value = new(window);
        return value;
    }

    public void DismissOnOutside(Action action, Func<bool> busy)
    {
        dismiss = action; keepOpen = busy;
        UpdateWatchers();
    }

    public bool HasMenu => menus.Any(pair => pair.Value == window && pair.Key.IsOpen);
    public bool IsBusy => HasMenu || external is not null || window.IsMouseCaptureWithin || keepOpen?.Invoke() == true || HasDropDown(window);

    public static bool HasChildPopup(Window owner) => For(owner).HasMenu || HasDropDown(owner);

    static bool HasDropDown(Window owner) => Descendants(owner).OfType<ComboBox>().Any(combo => combo.IsDropDownOpen);

    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        if (root is not Visual && root is not System.Windows.Media.Media3D.Visual3D) yield break;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); ++i)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    public static Window? TrayCallbackOwner => instances.Values.FirstOrDefault(value => value.window.IsVisible && value.external?.ProcessId == Environment.ProcessId)?.window;

    public static void Open(ContextMenu menu, FrameworkElement target)
    {
        menu.PlacementTarget = target;
        var owner = Window.GetWindow(target);
        if (owner is not null)
        {
            ++For(owner).generation;
            foreach (var existing in menus.Where(pair => pair.Value == owner).Select(pair => pair.Key).ToArray())
                if (existing != menu) existing.IsOpen = false;
        }
        menu.IsOpen = true;
    }

    static void MenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var owner = menu.PlacementTarget is { } target ? Window.GetWindow(target) : TrayCallbackOwner;
        menus[menu] = owner;
        menuWindows[menu] = (PresentationSource.FromVisual(menu) as HwndSource)?.Handle ?? 0;
        if (owner is not null)
        {
            var state = For(owner);
            ++state.generation;
            state.Signal();
        }
        UpdateWatchers();
    }

    static void MenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || !menus.Remove(menu, out var owner)) return;
        menuWindows.Remove(menu, out var menuWindow);
        if (owner is not null && instances.TryGetValue(owner, out var state))
        {
            var foreground = Native.GetForegroundWindow();
            if (foreground == menuWindow || state.OwnsForeground(foreground))
            {
                state.acceptInactive = true;
                state.acceptedForeground = foreground;
            }
            state.Signal();
            state.Review();
        }
        UpdateWatchers();
    }

    static void MenuKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is ContextMenu menu && menus.TryGetValue(menu, out var owner) && owner is not null
            && instances.TryGetValue(owner, out var state)) state.external?.SetEscapeIntent(e.Key == Key.Escape);
    }

    public void BeginTrayMenu(SystemTrayItem item)
    {
        if (disposed) return;
        ++generation;
        external?.Dispose();
        acceptInactive = false;
        external = new(window, item);
        poll.Start();
        Signal(); UpdateWatchers();
    }

    public void BeginTrayHover(SystemTrayItem item)
    {
        if (disposed || external is not null) return;
        ++generation;
        acceptInactive = false;
        external = new(window, item, true);
        external.CompleteRequest();
        poll.Start();
        Signal(); UpdateWatchers();
    }

    public void CompleteTrayHover(SystemTrayItem item)
    {
        if (external is { IsHover: true } session && session.Matches(item)) session.CompleteRequest(160);
    }

    public void RequestTrayFocus(SystemTrayItem item) => external?.RequestFocus(item);

    public void CompleteTrayMenu(bool sent)
    {
        if (external is null) return;
        if (sent) external.CompleteRequest();
        else EndTrayMenu(false);
    }

    void Tick(object? sender, EventArgs e)
    {
        if (external is not { } session) { poll.Stop(); return; }
        if (!session.Update(HasMenu)) EndTrayMenu(session.PreserveSource);
    }

    void EndTrayMenu(bool preserve)
    {
        if (external is not { } session) return;
        var restoreFocus = preserve && !session.IsHover && (session.EscapePressed || session.ReturnFocusRequested) && session.CanRestoreFocus();
        external = null;
        session.Dispose(); poll.Stop();
        acceptInactive = preserve;
        acceptedForeground = Native.GetForegroundWindow();
        if (restoreFocus && window.IsVisible && Native.SetForegroundWindow(new WindowInteropHelper(window).Handle))
            TrayFocusRestored?.Invoke(session.Item);
        Signal(); UpdateWatchers(); Review();
    }

    void Activated(object? sender, EventArgs e) { acceptInactive = false; }
    void Deactivated(object? sender, EventArgs e) => Review();
    void CaptureChanged(object sender, MouseEventArgs e) { Signal(); Review(); }
    void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!window.IsVisible)
        {
            ++generation;
            acceptInactive = false;
            consumedRelease = null;
            CloseMenus();
            external?.Dispose(); external = null; poll.Stop();
        }
        UpdateWatchers();
    }
    void Closed(object? sender, EventArgs e) => Dispose();

    void PointerDown(object sender, MouseButtonEventArgs e)
    {
        if (unchecked((uint)e.Timestamp) != inputTime)
        {
            inputTime = unchecked((uint)e.Timestamp); ++inputSequence; dismissedAnchor = null;
        }
        acceptInactive = false;
    }

    void PointerUp(object sender, MouseButtonEventArgs e)
    {
        if (consumedRelease is not { } press) return;
        consumedRelease = null;
        if (press.Sequence == inputSequence && press.Button == e.ChangedButton) e.Handled = true;
    }

    void KeyDown(object sender, KeyEventArgs e) => ResetDismissalGesture();

    public static void ResetDismissalGesture() => dismissedAnchor = null;

    public static bool WasAnchorDismissed(FrameworkElement anchor)
    {
        if (dismissedAnchor is not { } last || last.Sequence != inputSequence
            || !last.Anchor.TryGetTarget(out var target) || target != anchor) return false;
        dismissedAnchor = null;
        return true;
    }

    void Review()
    {
        if (reviewQueued || disposed || dismiss is null) return;
        reviewQueued = true;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            reviewQueued = false;
            if (disposed || !window.IsVisible || window.IsActive || IsBusy) return;
            var foreground = Native.GetForegroundWindow();
            if (foreground == 0 || OwnsForeground(foreground) || (acceptInactive && foreground == acceptedForeground)) return;
            dismiss?.Invoke();
        });
    }

    bool OwnsForeground(nint candidate)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return false;
        for (var i = 0; candidate != 0 && i < 16; ++i)
        {
            if (candidate == handle) return true;
            candidate = Native.GetWindow(candidate, Native.GW_OWNER);
        }
        return false;
    }

    void Signal() => Changed?.Invoke();

    void OutsidePointer(Native.Point point)
    {
        if (keepOpen?.Invoke() == true) return;
        if (Anchor is { } anchor && Contains(anchor, point))
            dismissedAnchor = (new(anchor), inputSequence);
        acceptInactive = false;
        var currentGeneration = generation;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (disposed || !window.IsVisible || generation != currentGeneration || keepOpen?.Invoke() == true) return;
            CloseMenus();
            external?.Dispose(); external = null; poll.Stop();
            Signal(); UpdateWatchers();
            dismiss?.Invoke();
        });
    }

    void CloseMenus()
    {
        foreach (var menu in menus.Where(pair => pair.Value == window).Select(pair => pair.Key).ToArray()) menu.IsOpen = false;
    }

    static bool Contains(FrameworkElement element, Native.Point point)
    {
        if (!element.IsVisible || PresentationSource.FromVisual(element) is null) return false;
        try
        {
            var origin = element.PointToScreen(new Point());
            var end = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
            return point.X >= Math.Min(origin.X, end.X) && point.X < Math.Max(origin.X, end.X)
                && point.Y >= Math.Min(origin.Y, end.Y) && point.Y < Math.Max(origin.Y, end.Y);
        }
        catch (InvalidOperationException) { return false; }
    }

    static bool ContainsDropDown(Window owner, Native.Point point) => Descendants(owner).OfType<ComboBox>()
        .Any(combo => combo.IsDropDownOpen && combo.Template?.FindName("PART_Popup", combo) is Popup { IsOpen: true, Child: FrameworkElement child }
            && Contains(child, point));

    static bool ContainsMenu(ContextMenu menu, Native.Point point)
    {
        if (menu.IsOpen && Contains(menu, point)) return true;
        return ContainsSubmenu(menu, point);
    }

    static bool ContainsSubmenu(ItemsControl parent, Native.Point point)
    {
        foreach (var item in parent.Items.OfType<MenuItem>().Where(item => item.IsSubmenuOpen))
        {
            if (item.Template?.FindName("PART_Popup", item) is Popup { IsOpen: true, Child: FrameworkElement child } && Contains(child, point)) return true;
            if (ContainsSubmenu(item, point)) return true;
        }
        return false;
    }

    static void UpdateWatchers()
    {
        var needed = menus.Count > 0 || instances.Values.Any(value => value.window.IsVisible && (value.dismiss is not null || value.external is not null));
        if (needed && mouseHook == 0) mouseHook = SetWindowsHookEx(14, mouseProc, GetModuleHandle(null), 0);
        if (needed && foregroundHook == 0) foregroundHook = Native.SetWinEventHook(3, 3, 0, foregroundProc, 0, 0, 0);
        if (needed && menuHook == 0) menuHook = Native.SetWinEventHook(4, 7, 0, menuProc, 0, 0, 0);
        if (needed) return;
        if (mouseHook != 0) UnhookWindowsHookEx(mouseHook);
        if (foregroundHook != 0) Native.UnhookWinEvent(foregroundHook);
        if (menuHook != 0) Native.UnhookWinEvent(menuHook);
        mouseHook = foregroundHook = menuHook = 0;
    }

    static nint MouseEvent(int code, nint wParam, nint lParam)
    {
        if (code < 0) return CallNextHookEx(mouseHook, code, wParam, lParam);
        try
        {
            MouseButton? button = (int)wParam switch { 0x201 => MouseButton.Left, 0x204 => MouseButton.Right, 0x207 => MouseButton.Middle,
                0x20B => (Marshal.ReadInt32(lParam, 8) >> 16) == 2 ? MouseButton.XButton2 : MouseButton.XButton1, _ => null };
            if (code >= 0 && button is not null)
            {
                var point = Marshal.PtrToStructure<Native.Point>(lParam);
                inputTime = unchecked((uint)Marshal.ReadInt32(lParam, 16)); ++inputSequence; dismissedAnchor = null;
                foreach (var state in instances.Values.Where(value => value.window.IsVisible).ToArray())
                {
                    state.external?.SetEscapeIntent(false);
                    if (state.keepOpen?.Invoke() == true) continue;
                    var owned = menus.Where(pair => pair.Value == state.window).Select(pair => pair.Key).ToArray();
                    var inMenu = owned.Any(menu => ContainsMenu(menu, point));
                    var inWindow = Contains(state.window, point);
                    if (owned.Length > 0 && !inMenu && inWindow) state.consumedRelease = (button.Value, inputSequence);
                    if (inMenu || inWindow || ContainsDropDown(state.window, point) || state.external?.Contains(point) == true) continue;
                    if (state.dismiss is not null || state.external is not null) state.OutsidePointer(point);
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { }
        return CallNextHookEx(mouseHook, code, wParam, lParam);
    }

    static void ForegroundEvent(nint hook, uint ev, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        try
        {
            var foreground = Native.GetForegroundWindow();
            foreach (var state in instances.Values.Where(value => value.window.IsVisible).ToArray())
            {
                if (state.external is { } session && !session.AllowsForeground(foreground)) state.EndTrayMenu(false);
                state.Review();
            }
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { }
    }

    static void MenuEvent(nint hook, uint ev, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        foreach (var state in instances.Values.ToArray()) state.external?.Observe(ev, hwnd, thread);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ++generation;
        CloseMenus(); external?.Dispose(); external = null; poll.Stop();
        window.Deactivated -= Deactivated; window.Activated -= Activated;
        window.LostMouseCapture -= CaptureChanged;
        window.PreviewMouseDown -= PointerDown; window.PreviewMouseUp -= PointerUp; window.PreviewKeyDown -= KeyDown;
        window.IsVisibleChanged -= VisibilityChanged; window.Closed -= Closed;
        instances.Remove(window); Changed = null; TrayFocusRestored = null; UpdateWatchers();
    }

    delegate nint MouseHookProc(int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern nint SetWindowsHookEx(int id, MouseHookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandle(string? name);
}
