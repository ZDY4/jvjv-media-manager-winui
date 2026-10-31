using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using JvJvMediaManager.Controllers.MainPage;
using JvJvMediaManager.Services.MainPage;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels.MainPage;
using JvJvMediaManager.Views.MainPageParts;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;
using JvJvMediaManager.Services;
using Microsoft.Extensions.DependencyInjection;

namespace JvJvMediaManager.Views;

public sealed partial class MainPage : Page
{
    private readonly MainPageShellViewModel _shell;
    private readonly Services.MainPage.MainPageModuleFactory _modules;
    private readonly IContentDialogService _dialogService;
    private readonly KeyEventHandler _keyDownHandler;
    private MainPageShellController? _controller;
    private MainPageShortcutRouter? _shortcutRouter;
    private BossKeyController? _bossKeyController;
    private bool _isResizingDockedPlayer;
    private bool _isShutdown;

    public MainPage()
    {
        _shell = ((App)Application.Current).Services.GetRequiredService<MainPageShellViewModel>();
        _modules = ((App)Application.Current).MainPageModules;
        _dialogService = _modules.CreateContentDialogService();
        _keyDownHandler = MainPage_KeyDown;
        InitializeComponent();
        DockedPlayerHost.DetachRequested += DockedPlayerHost_DetachRequested;
        DockedPlayerHost.CloseRequested += CloseDockedSession;
        DockedPlayerHost.SessionActivated += DockedPlayerHost_SessionActivated;
        DockedPlayerHost.ContextMenuRequested += DockedPlayerHost_ContextMenuRequested;
        DockedPlayerHost.MediaTagsChanged += DockedPlayerHost_MediaTagsChanged;
        DockedPlayerHost.MediaDeleted += DockedPlayerHost_MediaDeleted;
        DataContext = _shell;
        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;
    }

    public Grid RootLayout => RootLayoutHost;

    public SplitView LibrarySplitView => LibrarySplitViewHost;

    public LibraryShellViewModel Library => _shell.Library;

    public PlayerShellViewModel Player => _shell.Player;

    internal event Action<MediaPlayerSessionTransfer>? DockedSessionDetachRequested;
    internal event EventHandler? DockedSessionActivated;
    internal event EventHandler? DockedSessionClosed;
    internal event EventHandler<DockedSessionContextMenuRequestedEventArgs>? DockedSessionContextMenuRequested;
    internal event EventHandler<IndependentMediaWindowTagsChangedEventArgs>? DockedSessionMediaTagsChanged;
    internal event EventHandler<IndependentMediaWindowDeletedEventArgs>? DockedSessionMediaDeleted;

    internal bool HasDockedSession => DockedPlayerBorder.Visibility == Visibility.Visible;

    internal void ShowDockedSession(MediaPlayerSessionTransfer session)
    {
        DockedPlayerHost.LoadSession(session);
        DockSplitterColumn.Width = new GridLength(5);
        DockedPlayerColumn.Width = new GridLength(0.46, GridUnitType.Star);
        DockSplitter.Visibility = Visibility.Visible;
        DockedPlayerBorder.Visibility = Visibility.Visible;
    }

    internal void CloseDockedSession()
    {
        DockedPlayerHost.ClearSession();
        DockSplitterColumn.Width = new GridLength(0);
        DockedPlayerColumn.Width = new GridLength(0);
        DockSplitter.Visibility = Visibility.Collapsed;
        DockedPlayerBorder.Visibility = Visibility.Collapsed;
        DockedSessionClosed?.Invoke(this, EventArgs.Empty);
    }

    internal bool TryPlayInDockedSession(IndependentMediaWindowMedia media)
    {
        if (!HasDockedSession || !DockedPlayerHost.HasActiveSession)
        {
            return false;
        }

        DockedPlayerHost.PlayMedia(media);
        return true;
    }

    internal Task<bool> TryHandleDockedShortcutAsync(VirtualKey key)
    {
        if (!HasDockedSession || !DockedPlayerHost.HasActiveSession)
        {
            return Task.FromResult(false);
        }

        return DockedPlayerHost.TryHandleShortcutAsync(key);
    }

    internal void ApplyTagsToDockedSession(string mediaId, IReadOnlyList<string> tags)
    {
        if (!HasDockedSession)
        {
            return;
        }

        DockedPlayerHost.ApplyExternalTags(mediaId, tags);
    }

    internal void RemoveDeletedMediaFromDockedSession(IReadOnlyList<string> mediaIds)
    {
        if (!HasDockedSession || mediaIds.Count == 0)
        {
            return;
        }

        DockedPlayerHost.RemoveDeletedMedia(mediaIds);
    }

    public void FocusKeyboardHost()
    {
        KeyboardFocusHost.Focus(FocusState.Programmatic);
    }

    public bool TryHandleWindowShortcutKey(VirtualKey key)
    {
        if (_shortcutRouter == null || !_shortcutRouter.ShouldHandleWindowShortcutKey(key))
        {
            return false;
        }

        _ = HandleWindowShortcutKeyAsync(key);
        return true;
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _dialogService.AttachHost(XamlRoot);

            _controller ??= new MainPageShellController(this, _shell, LibraryPaneHost, PlayerPaneHost, _dialogService, _modules);
            _shortcutRouter ??= new MainPageShortcutRouter(_controller);

            RemoveHandler(UIElement.KeyDownEvent, _keyDownHandler);
            AddHandler(UIElement.KeyDownEvent, _keyDownHandler, true);

            FocusKeyboardHost();

            await _controller.InitializeAsync();
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MainPage", "MainPage_Loaded failed.", ex);
            await _dialogService.ShowInfoAsync("初始化失败", ex.Message);
        }
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        Shutdown();
    }

    internal void Shutdown()
    {
        RemoveHandler(UIElement.KeyDownEvent, _keyDownHandler);
        if (_isShutdown)
        {
            return;
        }

        _isShutdown = true;
        DockedPlayerHost.DetachRequested -= DockedPlayerHost_DetachRequested;
        DockedPlayerHost.CloseRequested -= CloseDockedSession;
        DockedPlayerHost.SessionActivated -= DockedPlayerHost_SessionActivated;
        DockedPlayerHost.ContextMenuRequested -= DockedPlayerHost_ContextMenuRequested;
        DockedPlayerHost.MediaTagsChanged -= DockedPlayerHost_MediaTagsChanged;
        DockedPlayerHost.MediaDeleted -= DockedPlayerHost_MediaDeleted;
        DockedPlayerHost.Dispose();
        _controller?.Dispose();
        _controller = null;
        _shortcutRouter = null;
    }

    private void DockedPlayerHost_DetachRequested(MediaPlayerSessionTransfer session)
    {
        DockedSessionDetachRequested?.Invoke(session);
    }

    private void DockedPlayerHost_SessionActivated(object? sender, EventArgs e)
    {
        DockedSessionActivated?.Invoke(this, e);
    }

    private void DockedPlayerHost_ContextMenuRequested(object? sender, DockedSessionContextMenuRequestedEventArgs e)
    {
        DockedSessionContextMenuRequested?.Invoke(this, e);
    }

    private void DockedPlayerHost_MediaTagsChanged(object? sender, IndependentMediaWindowTagsChangedEventArgs e)
    {
        DockedSessionMediaTagsChanged?.Invoke(this, e);
    }

    private void DockedPlayerHost_MediaDeleted(object? sender, IndependentMediaWindowDeletedEventArgs e)
    {
        DockedSessionMediaDeleted?.Invoke(this, e);
    }

    private void PlayerDockHost_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(MediaPlayerSessionTransfer.DataFormat)) return;
        e.AcceptedOperation = DataPackageOperation.Move;
        DockPreview.Visibility = Visibility.Visible;
        e.DragUIOverride.Caption = "停靠到右侧分屏";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private void PlayerDockHost_DragLeave(object sender, DragEventArgs e) => DockPreview.Visibility = Visibility.Collapsed;

    private async void PlayerDockHost_Drop(object sender, DragEventArgs e)
    {
        DockPreview.Visibility = Visibility.Collapsed;
        var deferral = e.GetDeferral();
        try
        {
            var value = await e.DataView.GetDataAsync(MediaPlayerSessionTransfer.DataFormat) as string;
            var session = value == null ? null : MediaPlayerSessionTransfer.Deserialize(value);
            if (session == null || session.MediaItems.Count == 0) return;
            ShowDockedSession(session);
            e.AcceptedOperation = DataPackageOperation.Move;
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("PlayerDock", "Failed to dock transferred player session.", ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void DockSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isResizingDockedPlayer = true;
        DockSplitter.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void DockSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingDockedPlayer || PlayerDockHost.ActualWidth <= 0) return;
        var pointerX = e.GetCurrentPoint(PlayerDockHost).Position.X;
        var minWidth = Math.Min(280, PlayerDockHost.ActualWidth * 0.4);
        var maxWidth = Math.Max(minWidth, PlayerDockHost.ActualWidth * 0.68);
        var dockedWidth = Math.Clamp(PlayerDockHost.ActualWidth - pointerX, minWidth, maxWidth);
        DockedPlayerColumn.Width = new GridLength(dockedWidth);
        e.Handled = true;
    }

    private void DockSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isResizingDockedPlayer = false;
        DockSplitter.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void DockSplitter_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isResizingDockedPlayer = false;
    }

    public Task HandleAddFolderFromTitleBarAsync()
    {
        return _controller?.HandleAddFolderFromTitleBarAsync() ?? Task.CompletedTask;
    }

    public Task HandleAddFilesFromTitleBarAsync()
    {
        return _controller?.HandleAddFilesFromTitleBarAsync() ?? Task.CompletedTask;
    }

    public Task HandleOpenSettingsFromTitleBarAsync()
    {
        return _controller?.HandleOpenSettingsFromTitleBarAsync() ?? Task.CompletedTask;
    }

    public void ConfigureBossKey(BossKeyController bossKeyController)
    {
        _bossKeyController = bossKeyController;
    }

    private void BossKeyRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        _bossKeyController?.ExitBossMode();
    }

    public void ShowBossRecoveryButton()
    {
        BossKeyRecoveryButton.Visibility = Visibility.Visible;
    }

    public void ExitBossMode()
    {
        BossKeyRecoveryButton.Visibility = Visibility.Collapsed;
    }

    private async void MainPage_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            if (_shortcutRouter != null)
            {
                await _shortcutRouter.HandleKeyDownAsync(e);
            }
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MainPage", $"MainPage_KeyDown failed. Key={e.Key}.", ex);
        }
    }

    private async Task HandleWindowShortcutKeyAsync(VirtualKey key)
    {
        try
        {
            if (_shortcutRouter != null)
            {
                await _shortcutRouter.HandleVirtualKeyDownAsync(key);
            }
        }
        catch (Exception ex)
        {
            AppTraceLogger.LogException("MainPage", $"HandleWindowShortcutKeyAsync failed. Key={key}.", ex);
        }
    }
}
