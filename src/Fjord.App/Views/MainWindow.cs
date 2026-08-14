using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Fjord.Commands;
using Fjord.Config;
using Fjord.Core.Diagnostics;
using Fjord.Core.FileSystem;
using Fjord.Core.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fjord.App.Views;

/// <summary>
/// The application window.
/// </summary>
/// <remarks>
/// Built in code rather than XAML. With an immediate-mode list and a code-built status
/// line there is almost no markup to write, and avoiding generated XAML keeps
/// <c>TreatWarningsAsErrors</c> on for the whole solution.
/// </remarks>
public sealed class MainWindow : Window, IMessageSink
{
    private readonly FileListView _list;
    private readonly Workspace _workspace;
    private readonly ListTheme _theme;
    private readonly FjordPaths _paths;
    private readonly KeyBindings _bindings;
    private readonly KeyModeRegistry _modeRegistry;
    private readonly InputController _input;
    private readonly StackPanel _path;
    private readonly TextBlock _status;
    private readonly TextBlock _keys;
    private readonly CommandLine _commandLine;
    private readonly Border _pathBar;
    private readonly Border _statusBar;
    private readonly OperationHost _operations;
    private readonly HelpOverlay _help;
    private readonly Fjord.Storage.SqliteStorage _storage;
    private readonly SessionHost _sessions;
    private readonly TaskPanel _taskPanel;
    private readonly CommandRegistry _registry;
    private readonly KeyHints _keyHints;

    /// <summary>How far the pointer must travel before a press becomes a drag.</summary>
    /// <remarks>
    /// Without it a click with an unsteady hand redraws the tab bar under the pointer,
    /// which reads as the app twitching.
    /// </remarks>
    private const double DragThreshold = 4;

    // Which tab a drag started on, while one is in progress.
    private int? _dragFrom;
    private Point _dragOrigin;
    private bool _dragging;

    // What the breadcrumb strip was last built for, so it is not rebuilt on every repaint.
    private string _breadcrumbPath = string.Empty;

    // What the command being typed does, shown in the status bar while the line is open.
    private string _commandUsage = string.Empty;
    private readonly StackPanel _tabStrip;
    private readonly Border _tabBar;
    private readonly ConfigWatcher? _watcher;
    private readonly ConfigService _configService;

    // Built before the input controller, because :cd needs it and the controller is wired
    // late. The controller uses this same instance rather than owning one.
    private readonly Fjord.Core.Navigation.MarkFile _marks = new();
    private readonly ILogger _log;

    private FjordConfig _config;
    private string? _message;
    private MessageLevel _messageLevel;

    /// <param name="logging">
    /// Hands each collaborator its own category. Optional so a test can build a window
    /// without one, and so a failure to open the log file cannot stop the app starting.
    /// </param>
    public MainWindow(
        FjordPaths paths,
        ConfigLoadResult initial,
        IDirectoryLister lister,
        ILoggerFactory? logging = null)
    {
        _paths = paths;
        _log = logging?.CreateLogger<MainWindow>() ?? (ILogger)NullLogger.Instance;
        _config = initial.Config;
        _message = initial.Error;
        _messageLevel = initial.Error is null ? MessageLevel.Info : MessageLevel.Error;

        Title = "Fjord";

        // No system title bar. It is a strip of chrome showing a name the user already
        // knows, above a window whose own top row can carry the buttons instead — and on a
        // list this dense, one row back is several more files.
        //
        // BorderOnly rather than None: the frame stays, so the window still resizes from
        // its edges and snaps. Avalonia 12 dropped ExtendClientAreaChromeHints — with
        // decorations extended it now draws its own title bar into the client area, which
        // is the thing being removed here — so this is the way to say it.
        WindowDecorations = WindowDecorations.BorderOnly;
        Icon = LoadIcon();
        Width = 1000;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _theme = new ListTheme(_config.Appearance);
        _list = new FileListView(_theme);

        // Panes are created on demand so every tab gets its own snapshot and its own
        // cancellation source; that is what keeps a tab switch from waiting on anything.
        _workspace = new Workspace(
            () => new PaneController(
                lister,
                _list,
                _config.ToSortSpec(),
                _config.ToFilterSpec(),
                logging?.CreateLogger<PaneController>()),
            () => (Application.Current?.ApplicationLifetime
                as IClassicDesktopStyleApplicationLifetime)?.Shutdown());

        _bindings = new KeyBindings();
        _modeRegistry = new KeyModeRegistry();

        var platform = Fjord.Platform.PlatformServices.Create();
        _operations = new OperationHost(
            Fjord.Platform.TrashServices.Create(), logging?.CreateLogger<OperationHost>())
        {
            // Read at the moment of asking rather than captured, so :set takes effect
            // without a restart like every other setting.
            // _input is assigned further down this constructor and this closure only runs
            // on a key press, long after.
            Confirmation = (question, confirmed) =>
            {
                if (_config.Behaviour.ConfirmTrash)
                {
                    _input!.Ask(question, confirmed);
                }
                else
                {
                    confirmed();
                }
            },
            ClipboardWriter = CopyToClipboardAsync,
        };
        _taskPanel = new TaskPanel(_theme);
        _help = new HelpOverlay(_theme);

        _configService = new ConfigService(_paths, ReloadConfig);

        var registry = new CommandRegistry();
        _registry = registry;
        registry.Register(new NavigationCommands(platform, _marks, _configService));
        registry.Register(new PlatformCommands(platform));
        registry.Register(new OperationCommands(_operations, new FileOperationFactory()));
        registry.Register(new HelpCommands(
            registry,
            _bindings,
            _modeRegistry,
            _help,
            _configService,
            platform));

        // The mode manager asks the registry whether a command is an operator, and the
        // mode commands ask the input controller to change mode — so the two are built
        // with a late-bound reference to each other.
        InputController? controller = null;
        var modes = new ModeManager(_bindings, _modeRegistry, IsOperator);
        var parser = new CommandParser(registry);

        controller = new InputController(
            modes,
            _modeRegistry,
            parser,
            new OperatorDispatcher(parser, registry),
            this,
            () => _workspace.ActivePane,
            () => _workspace)
        {
            Marks = _marks,
        };
        _input = controller;

        registry.Register(new ModeCommands(controller));
        registry.Register(new SearchCommands(controller));
        registry.Register(new DefaultHandlerCommands(
            Fjord.Platform.FolderHandlers.Create(),
            controller,
            LauncherPath,
            RestartElevated));

        _storage = new Fjord.Storage.SqliteStorage(Path.Combine(_paths.Data, "fjord.db"));
        _sessions = new SessionHost(_workspace);
        registry.Register(new SessionCommands(_storage, _sessions));
        registry.Register(new ScrapbookCommands(_storage, _sessions));
        registry.Register(new TabCommands());

        _commandLine = new CommandLine(
            _theme,
            new CompletionEngine(
                registry,
                new PathCompletionSource(() => _workspace.ActivePane),
                SettingCompletions,
                new DestinationSource(_marks, _storage),
                () => _configService.ListingNames));

        DefaultBindings.Load(_bindings, _modeRegistry);
        ApplyUserBindings();

        _tabStrip = new StackPanel { Orientation = Orientation.Horizontal };
        // The tabs and the window buttons share one row: buttons pinned right, tabs taking
        // what is left. That row is the title bar now, so unlike before it is shown even
        // with a single tab — the close button has to live somewhere.
        var tabRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var buttons = BuildWindowButtons();
        Grid.SetColumn(buttons, 1);

        // A StackPanel lays its children out past its own width, so without this the
        // twentieth tab would be drawn underneath the close button.
        var tabs = new Panel { ClipToBounds = true, Children = { _tabStrip } };

        tabRow.Children.Add(tabs);
        tabRow.Children.Add(buttons);

        _tabBar = new Border
        {
            Background = _theme.Background,
            Child = tabRow,
        };

        _keyHints = new KeyHints(_theme);
        _path = new StackPanel { Orientation = Orientation.Horizontal };
        _status = Label(_theme.MutedText);
        _keys = Label(_theme.CursorBar);
        _pathBar = Bar(_path);
        _statusBar = Bar(BuildStatusRow());

        var scroller = new ScrollViewer
        {
            Content = _list,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        DockPanel.SetDock(_tabBar, Dock.Top);
        DockPanel.SetDock(_pathBar, Dock.Top);
        DockPanel.SetDock(_statusBar, Dock.Bottom);
        DockPanel.SetDock(_taskPanel, Dock.Bottom);

        // The command line, its panel and the key hints float over the listing rather than
        // docking under it. Docked, opening the command line took height from the list and
        // re-laid it out: the rows jumped, and the entry under the cursor moved while the
        // user was in the middle of typing a command about it. noPDF learned the same thing
        // and says so in its own layout.
        var overlay = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Bottom,
            Children = { _keyHints, _commandLine.CompletionHost, _commandLine },
        };

        Content = new DockPanel
        {
            Children =
            {
                _tabBar, _pathBar, _statusBar, _taskPanel,
                new Panel { Children = { scroller, _help, overlay } },
            },
        };

        ApplyTheme();

        // Tunnelling, so the mode manager sees a key before whatever has focus does. This
        // is what will let Escape leave the command line once it has a TextBox.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        // Bubbling, not tunnelling: by the time a press reaches the window every control
        // that wanted it has had it, so what is left is genuinely background.
        AddHandler(PointerPressedEvent, OnBackgroundPressed, RoutingStrategies.Bubble);

        _workspace.PaneChanged += (_, _) => UpdateChrome();
        _workspace.Changed += (_, _) =>
        {
            RebuildTabBar();
            UpdateChrome();
            RecordVisit();
        };
        _input.Handled += (_, _) => UpdateChrome();
        _input.CommandLineRequested += (_, initial) =>
        {
            _input.Modes.EnterMode(KeyMode.Command);
            _commandLine.Open(initial);
        };

        // What the command being typed does, in the status bar. noPDF puts it there rather
        // than in a panel of its own, and that is the better place: the line where messages
        // already appear is somewhere the eye is, and it costs nothing to dismiss.
        _commandLine.Usage = name => _registry.Find(name)?.Description;
        _commandLine.UsageChanged += (_, usage) =>
        {
            _commandUsage = usage;
            UpdateChrome();
        };

        _commandLine.LoadHistory = LoadCommandHistory;
        _commandLine.SaveHistory = SaveCommandHistory;

        _input.FrameStatsToggled += (_, _) =>
        {
            _list.ShowFrameStats = !_list.ShowFrameStats;
            _list.InvalidateVisual();
        };
        _input.FrameStatsEnabled = () => _list.ShowFrameStats;

        _commandLine.Accepted += (_, line) =>
        {
            _list.Focus();
            _input.LeaveCommandLine(_workspace.ActivePane);
            _input.Execute(line);
        };

        _commandLine.Cancelled += (_, _) =>
        {
            _list.Focus();
            _input.LeaveCommandLine(_workspace.ActivePane);
        };

        _operations.Changed += (_, _) => _taskPanel.Update(_operations.Active);

        // Filesystem watching is not in M1, so the listing has to be told when an
        // operation finished changing it. Once per operation, not per item.
        _operations.Settled += async (_, _) =>
        {
            _taskPanel.Update(_operations.Active);
            await _workspace.ActivePane.ReloadAsync();
        };

        try
        {
            _watcher = new ConfigWatcher(
                _paths.ConfigFile, () => ReloadConfig(), a => Dispatcher.UIThread.Post(a));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Hot reload is a convenience; losing it must not stop the app starting.
            _log.LogWarning(ex, "config watch unavailable");
            _message ??= $"config watch unavailable: {ex.Message}";
        }

        Closing += (_, _) => SaveSessionQuietly();

        Closed += async (_, _) =>
        {
            _watcher?.Dispose();
            _storage.Dispose();

            // Stops any copy still running, rather than leaving it writing after the
            // window is gone.
            _operations.CancelAll();
            await _operations.DisposeAsync();
        };

        Opened += async (_, _) =>
        {
            // An async event handler swallows whatever escapes it. Everything that gets the
            // first listing on screen happens in here, so a failure would leave an empty
            // window and an empty log — which is exactly what it did.
            try
            {
                ClampToScreen();
                _list.Focus();

                var pane = _workspace.Initialise();

                // The window is already up and interactive here, so restoring cannot delay
                // it — which was the whole point of not doing this before Opened.
                await RestoreOrOpenAsync(pane);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "startup failed after the window opened");
                _message = $"startup failed: {ex.Message}";
                _messageLevel = MessageLevel.Error;
                UpdateChrome();
            }
        };

        bool IsOperator(string name) => registry.Find(name)?.IsOperator == true;
    }

    /// <summary>
    /// Scrolls the help page with the same keys that move the file list.
    /// </summary>
    /// <remarks>
    /// Help is a page to read, not a mode, so it takes only movement keys and a way out.
    /// But a keyboard-driven app whose help needs the mouse to read is a poor advert for
    /// itself, so j/k, the arrows, Ctrl-d/u, gg/G and the page keys all work here as they
    /// do in the list.
    /// </remarks>
    private void HandleHelpKey(KeyEventArgs e)
    {
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        switch (e.Key)
        {
            case Key.Escape or Key.Q:
                _help.Hide();
                _list.Focus();
                break;

            case Key.J or Key.Down:
                _help.ScrollLines(1);
                break;

            case Key.K or Key.Up:
                _help.ScrollLines(-1);
                break;

            case Key.D when control:
                _help.ScrollPages(0.5);
                break;

            case Key.U when control:
                _help.ScrollPages(-0.5);
                break;

            case Key.F when control:
            case Key.PageDown or Key.Space:
                _help.ScrollPages(1);
                break;

            case Key.B when control:
            case Key.PageUp:
                _help.ScrollPages(-1);
                break;

            // G goes to the end, gg to the start — but a single g has nothing to buffer
            // against here, so Home is the pairing rather than a two-key sequence.
            case Key.G when shift:
            case Key.End:
                _help.ScrollToBottom();
                break;

            case Key.G:
            case Key.Home:
                _help.ScrollToTop();
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// The window and taskbar icon.
    /// </summary>
    /// <remarks>
    /// Loaded from an embedded resource rather than a file beside the executable, so it
    /// survives a single-file publish. A missing icon is not worth failing to start over —
    /// the window simply gets the platform default.
    /// </remarks>
    private static WindowIcon? LoadIcon()
    {
        try
        {
            // The authority of an avares URI is the assembly name, not the namespace, and
            // the two differ here: the project is Fjord.App and the executable is Fjord.
            // Hard-coding either would break the icon the day the other changed, and the
            // catch below would swallow it into a window that merely looks wrong.
            var assembly = typeof(MainWindow).Assembly.GetName().Name;
            using var stream = AssetLoader.Open(new Uri($"avares://{assembly}/Assets/fjord.ico"));
            return new WindowIcon(stream);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Restores the last session, or opens the working directory if there is none.
    /// </summary>
    /// <remarks>
    /// A failure here must not stop the app starting with an empty window: a corrupt or
    /// stale session is annoying, an app that will not open is not usable at all.
    /// </remarks>
    /// <summary>
    /// Puts text on the system clipboard.
    /// </summary>
    /// <remarks>
    /// The clipboard hangs off the TopLevel rather than the application, which is why this
    /// lives here and reaches the command layer as a delegate: Fjord.Commands has no window
    /// and should not learn about one for this.
    /// </remarks>
    private async Task CopyToClipboardAsync(string text)
    {
        var sink = (IMessageSink)this;

        if (Clipboard is not { } clipboard)
        {
            sink.Error("No clipboard available.");
            return;
        }

        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or PlatformNotSupportedException
                                       or System.Runtime.InteropServices.COMException)
        {
            // Another application can hold the clipboard open; on Windows that surfaces
            // here rather than as a failure instead of a wait.
            sink.Error($"Could not copy: {ex.Message}");
        }
    }

    /// <summary>Setting names for <c>:set</c>, each showing what the file says now.</summary>
    private IReadOnlyList<CompletionItem> SettingCompletions() =>
        [.. _configService.Settings.Select(s => new CompletionItem(
            s.Name, s.Current is null ? s.Doc : $"= {s.Current}  {s.Doc}"))];

    /// <summary>
    /// Folders named on the command line, which take precedence over the saved session.
    /// </summary>
    /// <remarks>
    /// Being asked to open something is an instruction; restoring a session is a courtesy.
    /// Someone who double-clicked a folder wants that folder, not the six tabs they had
    /// open yesterday — though those still come back behind it.
    /// </remarks>
    public IReadOnlyList<string> StartupPaths { get; init; } = [];

    /// <summary>
    /// Opens paths handed over by a second launch of Fjord.
    /// </summary>
    /// <remarks>
    /// Called from the pipe's own thread, so everything it touches has to be marshalled.
    /// </remarks>
    public void OpenFromAnotherLaunch(IReadOnlyList<string> paths) =>
        Dispatcher.UIThread.Post(() => Observe(OpenPathsAsync(paths, focus: true)));

    /// <summary>
    /// Opens each path in a tab of its own.
    /// </summary>
    /// <param name="focus">
    /// Whether to bring the window forward. True when another launch asked for this: the
    /// user clicked something and is waiting to see it, possibly behind other windows.
    /// </param>
    /// <remarks>
    /// A file rather than a folder opens its folder with the file under the cursor, which
    /// is what "show me this" means for something that is not a place.
    /// </remarks>
    private async Task OpenPathsAsync(IReadOnlyList<string> paths, bool focus)
    {
        var opened = false;

        foreach (var path in paths)
        {
            if (Resolve(path) is not var (directory, select))
            {
                continue;
            }

            // The first one can have the tab that is already here when it is still empty,
            // which is the ordinary "fjord ." case; anything after it gets its own.
            if (!opened && _workspace.ActivePane.Directory.Length == 0)
            {
                await _workspace.ActivePane.NavigateAsync(directory, select);
            }
            else
            {
                _workspace.OpenTab(directory, Core.Panes.NewTabPosition.End);

                if (select is not null)
                {
                    _workspace.ActivePane.MoveCursorToName(select);
                }
            }

            opened = true;
        }

        if (!opened)
        {
            return;
        }

        if (focus)
        {
            // Restoring first: a minimised window that is merely activated stays minimised
            // and the click looks as though it did nothing.
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
        }

        _list.Focus();
        UpdateChrome();
    }

    /// <summary>Splits a path into the directory to show and the entry to land on.</summary>
    private static (string Directory, string? Select)? Resolve(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);

            if (System.IO.Directory.Exists(full))
            {
                return (full, null);
            }

            if (File.Exists(full) && Path.GetDirectoryName(full) is { Length: > 0 } parent)
            {
                return (parent, Path.GetFileName(full));
            }
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or NotSupportedException
                                       or PathTooLongException
                                       or IOException)
        {
            // Whatever was on the command line is not a path this machine can address.
        }

        return null;
    }

    private async Task RestoreOrOpenAsync(PaneController pane)
    {
        try
        {
            // Marks before tabs: they are what the restored window navigates by, and they
            // cost one small query whether or not there is a session to go with them.
            _marks.Load(_storage.LoadMarks());

            if (_storage.LoadSession(Core.Storage.SavedSession.DefaultName) is { Tabs.Count: > 0 } session)
            {
                await _sessions.RestoreAsync(session);
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _message = $"session not restored: {ex.Message}";
            _messageLevel = MessageLevel.Warning;
        }

        if (StartupPaths.Count > 0)
        {
            await OpenPathsAsync(StartupPaths, focus: false);

            if (pane.Directory.Length > 0)
            {
                return;
            }
        }

        await pane.NavigateAsync(Environment.CurrentDirectory);
    }

    /// <summary>Saves the session on the way out, without letting a failure block closing.</summary>
    private void SaveSessionQuietly()
    {
        try
        {
            _storage.SaveSession(_sessions.Capture(Core.Storage.SavedSession.DefaultName));
            _storage.SaveMarks(_marks.All);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Nothing useful to show: the window is already going away.
        }
    }

    /// <summary>
    /// Shrinks the window to fit the screen it opened on.
    /// </summary>
    /// <remarks>
    /// The default 1000x700 is in device-independent pixels, and on a high-DPI display the
    /// usable desktop can be smaller than that: a 2560x1440 screen at 250% scaling offers
    /// 1024x576 DIP, so 700 does not fit. The overflow lands at the bottom, which is
    /// exactly where the status bar and command line live — they end up off-screen and the
    /// app looks like it has neither.
    /// </remarks>
    private void ClampToScreen()
    {
        if (Screens.ScreenFromWindow(this) is not { } screen)
        {
            return;
        }

        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var available = new Size(
            screen.WorkingArea.Width / scaling, screen.WorkingArea.Height / scaling);

        // A margin for the title bar and shadow, so the window is fully reachable.
        var width = Math.Min(Width, available.Width - 40);
        var height = Math.Min(Height, available.Height - 60);

        if (width < Width || height < Height)
        {
            Width = Math.Max(400, width);
            Height = Math.Max(300, height);
            Position = new PixelPoint(
                screen.WorkingArea.X + (int)((screen.WorkingArea.Width - (Width * scaling)) / 2),
                screen.WorkingArea.Y + (int)((screen.WorkingArea.Height - (Height * scaling)) / 2));
        }
    }

    void IMessageSink.Message(MessageLevel level, string text)
    {
        _message = text;
        _messageLevel = level;
        UpdateChrome();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // The command line owns the keyboard while it is open. Tunnelling means this
        // handler runs first, so without the guard every letter typed would be matched
        // against normal-mode bindings instead of reaching the text box.
        // Escape closes the command line from anywhere, whatever holds focus and whatever
        // state the bar thinks it is in. A panel with no way to dismiss it is the worst
        // failure this window has, and it has happened twice; this is the backstop that
        // does not depend on the part that broke.
        if (e.Key == Key.Escape && (_commandLine.IsOpen || _commandLine.HasPanel))
        {
            _commandLine.Close();
            _input.LeaveCommandLine(_workspace.ActivePane);
            _list.Focus();
            e.Handled = true;
            return;
        }

        if (_commandLine.IsOpen)
        {
            // Unless it does not actually have the keyboard. Open-but-unfocused swallows
            // everything — this handler defers, and the text box never sees the key — so
            // the bar sits there with its panel up and no key will close it. Escape gets
            // out; anything else puts the focus back where it should have been.
            if (!_commandLine.HasKeyboard)
            {
                _commandLine.FocusInput();
                e.Handled = true;
            }

            return;
        }

        if (_help.IsVisible)
        {
            HandleHelpKey(e);
            return;
        }

        if (_input.HandleKey(e))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Re-reads the config file and applies whatever parsed.
    /// </summary>
    /// <remarks>
    /// A file that fails to parse leaves the previous settings in place and reports the
    /// error. Swapping in defaults on every stray tab would make editing the file while
    /// the app runs unusable.
    /// </remarks>
    private string? ReloadConfig()
    {
        var result = ConfigFile.Load(_paths.ConfigFile);

        if (!result.Succeeded)
        {
            _log.LogWarning("config reload rejected, keeping the previous settings: {Error}", result.Error);
            _message = result.Error;
            _messageLevel = MessageLevel.Error;
            UpdateChrome();
            return result.Error;
        }

        _log.LogInformation("config reloaded from {Path}", _paths.ConfigFile);
        _config = result.Config;
        _message = null;

        _theme.Apply(_config.Appearance);
        ApplyTheme();
        ApplyUserBindings();

        // Reorders what is already loaded; no directory is re-read.
        foreach (var tab in _workspace.Tabs)
        {
            // Only panes that never chose an order of their own. Saving the config should
            // not undo a tab the user deliberately sorted by size.
            if (tab.Active is PaneController pane)
            {
                pane.ApplyDefaultOrder(_config.ToSortSpec(), _config.ToFilterSpec());
            }
        }

        _list.RefreshTheme();
        _help.RefreshTheme();
        UpdateChrome();
        return null;
    }

    private void ApplyUserBindings()
    {
        _bindings.ClearUserBindings();

        var problems = DefaultBindings.Apply(_bindings, _modeRegistry, _config.Bindings);
        if (problems.Count > 0)
        {
            _message = "bindings: " + string.Join("; ", problems);
            _messageLevel = MessageLevel.Warning;
        }
    }

    /// <summary>
    /// Shows what could follow a half-typed sequence, and nothing otherwise.
    /// </summary>
    /// <remarks>
    /// No timer this time. The chips are one line and appear beside the keys you are
    /// already typing, so there is nothing to wait out — the delay existed to stop a
    /// full-height list flashing past, and a full-height list was the wrong shape.
    /// </remarks>
    private void UpdateKeyHints()
    {
        var buffered = _input.Modes.Buffered;
        var pending = _input.Modes.PendingOperator is not null;

        // The command line has its own panel; two stacked would be a wall again.
        if (_commandLine.IsOpen || (buffered.Count == 0 && !pending))
        {
            _keyHints.Hide();
            return;
        }

        _keyHints.Show(
        [
            .. Continuations(_input.Modes.Mode, buffered)
                .Select(c => (c.Keys.ToString(), Describe(c.Command)))
                .OrderBy(c => c.Item1, StringComparer.Ordinal),
        ]);
    }

    /// <summary>
    /// Continuations for a mode and everything it falls back to.
    /// </summary>
    /// <remarks>
    /// A scrapbook that listed only its own four keys would suggest the motions had gone.
    /// The specific mode's binding for a sequence wins, since that is what pressing it will
    /// actually do.
    /// </remarks>
    private IReadOnlyList<(Core.Keys.KeySequence Keys, string Command)> Continuations(
        KeyMode mode, Core.Keys.KeySequence buffered)
    {
        var seen = new Dictionary<string, (Core.Keys.KeySequence Keys, string Command)>(StringComparer.Ordinal);
        var current = mode;

        for (var depth = 0; depth < 8; depth++)
        {
            foreach (var candidate in _bindings.Continuations(current, buffered))
            {
                seen.TryAdd(candidate.Keys.ToString(), candidate);
            }

            if (_modeRegistry.FallbackFor(current) is not { } next || next == current)
            {
                break;
            }

            current = next;
        }

        return [.. seen.Values];
    }

    /// <summary>What a binding does, named as briefly as a chip allows.</summary>
    private string Describe(string command)
    {
        var name = (command.StartsWith(':') ? command[1..] : command).Split(' ')[0];
        return _registry.Find(name)?.Name ?? name;
    }

    /// <summary>
    /// The command history, kept beside the session database.
    /// </summary>
    /// <remarks>
    /// A plain text file rather than a table: it is a list of lines, the user may well want
    /// to read or clear it by hand, and it relates to nothing else that is stored.
    /// </remarks>
    private string CommandHistoryPath => Path.Combine(_paths.Data, "command-history.txt");

    private IReadOnlyList<string> LoadCommandHistory()
    {
        try
        {
            return File.Exists(CommandHistoryPath) ? File.ReadAllLines(CommandHistoryPath) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "could not read the command history");
            return [];
        }
    }

    private void SaveCommandHistory(IReadOnlyList<string> entries)
    {
        try
        {
            FjordPaths.EnsureDirectoryFor(CommandHistoryPath);
            File.WriteAllLines(CommandHistoryPath, entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the history is not worth interrupting the command that just ran.
            _log.LogWarning(ex, "could not write the command history");
        }
    }

    /// <summary>
    /// Moves the window when the press landed on nothing that wanted it.
    /// </summary>
    /// <remarks>
    /// The whole surface drags: the tab bar's empty space, the path bar, the status bar and
    /// the listing. There is nothing to aim for, which matters more here than in most
    /// windows because the title bar is the one piece of chrome a keyboard-driven file
    /// manager gives no other reason to look at.
    /// <para>
    /// Anything interactive marks the event handled — a tab chip, a breadcrumb, a
    /// completion row, the text box — and this never sees it. That is why the handler
    /// bubbles rather than tunnels, and why it checks rather than guessing at coordinates.
    /// </para>
    /// </remarks>
    private void OnBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // A double click is the system's "maximise", and starting a drag eats it.
        if (e.ClickCount > 1)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;

            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
    }

    /// <summary>
    /// Shows a tab as picked up, and where it would land.
    /// </summary>
    /// <remarks>
    /// The dragged chip is dimmed and the one it would displace gets an accent edge on the
    /// side it would arrive from. Nothing moved until release, so without this the gesture
    /// had no feedback at all and looked like nothing was happening.
    /// </remarks>
    private void ShowDrag(int from, int? to)
    {
        for (var i = 0; i < _tabStrip.Children.Count; i++)
        {
            if (_tabStrip.Children[i] is not Border chip)
            {
                continue;
            }

            var isSource = i == from;

            chip.Opacity = isSource ? 0.45 : 1;
            chip.BorderBrush = _theme.CursorBar;
            chip.BorderThickness = i == to && to != from
                ? new Thickness(to > from ? 0 : 2, 0, to > from ? 2 : 0, 0)
                : default;
        }
    }

    /// <summary>Which tab position a pointer release lands on, or null when it missed.</summary>
    private int? DropTargetAt(Point position)
    {
        for (var i = 0; i < _tabStrip.Children.Count; i++)
        {
            var child = _tabStrip.Children[i];
            var left = child.Bounds.Left;

            if (position.X >= left && position.X <= left + child.Bounds.Width)
            {
                return i;
            }
        }

        // Past the last tab means the end, which is what dragging off the right implies.
        return position.X > 0 && _tabStrip.Children.Count > 0 ? _tabStrip.Children.Count - 1 : null;
    }

    /// <summary>
    /// Rebuilds the path bar as one clickable segment per directory.
    /// </summary>
    /// <remarks>
    /// The path was a single label, so the only way up three levels was three presses of h
    /// or typing the path out. Each segment carries the path up to and including itself, so
    /// clicking is exactly "go there" with no arithmetic at the click site.
    /// </remarks>
    private void RebuildBreadcrumbs(string directory)
    {
        if (string.Equals(_breadcrumbPath, directory, StringComparison.Ordinal))
        {
            return;
        }

        _breadcrumbPath = directory;
        _path.Children.Clear();

        // A scrapbook label is not a path and has no segments to walk up through.
        if (directory.Length == 0 || directory.Contains(':') && !Path.IsPathRooted(directory))
        {
            _path.Children.Add(Label(_theme.FileText, directory));
            return;
        }

        var accumulated = string.Empty;
        var first = true;

        foreach (var segment in Segments(directory))
        {
            accumulated = accumulated.Length == 0 ? segment : Path.Combine(accumulated, segment);

            if (!first)
            {
                _path.Children.Add(Label(_theme.MutedText, " " + Path.DirectorySeparatorChar + " "));
            }

            first = false;
            _path.Children.Add(Crumb(segment, accumulated));
        }
    }

    /// <summary>The parts of a path, with the root — drive or leading slash — kept whole.</summary>
    private static IEnumerable<string> Segments(string directory)
    {
        var root = Path.GetPathRoot(directory) ?? string.Empty;

        if (root.Length > 0)
        {
            yield return root;
        }

        var rest = directory[root.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in rest)
        {
            yield return part;
        }
    }

    private Control Crumb(string text, string target)
    {
        var block = Label(_theme.FileText, text);

        var button = new Border
        {
            Child = block,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        // Highlighted on hover, so it reads as clickable rather than as a label that
        // happens to respond.
        button.PointerEntered += (_, _) => button.Background = _theme.CursorBackground;
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;

        button.PointerPressed += (_, e) =>
        {
            _list.Focus();
            Observe(_workspace.ActivePane.NavigateAsync(target));
            e.Handled = true;
        };

        return button;
    }

    /// <summary>
    /// The path the system should run to start Fjord.
    /// </summary>
    /// <remarks>
    /// <see cref="Environment.ProcessPath"/>, not the assembly location. A single-file
    /// publish extracts its managed assemblies to a temporary directory, so the assembly
    /// lives somewhere that will not exist next week — registering that as the folder
    /// handler works until the first reboot clears the extraction and then silently stops.
    /// </remarks>
    private static string LauncherPath() =>
        Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];

    /// <summary>
    /// Starts Fjord again with administrator rights and closes this one.
    /// </summary>
    /// <returns>False when it did not start, including when the user dismissed the prompt.</returns>
    /// <remarks>
    /// A separate process rather than an in-place elevation, because there is no such
    /// thing: Windows grants privileges at process creation. The session is saved first,
    /// so the elevated window comes up looking like the one being replaced.
    /// </remarks>
    private bool RestartElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            SaveSessionQuietly();

            var started = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = LauncherPath(),
                UseShellExecute = true,

                // What actually raises the prompt. Without it this is just another copy.
                Verb = "runas",
            });

            if (started is null)
            {
                return false;
            }

            Close();
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Cancelling the UAC prompt arrives here as ERROR_CANCELLED. Declining is an
            // answer, not a failure, so it is reported the same as any other "no".
            _log.LogInformation(ex, "elevated restart declined or refused");
            return false;
        }
    }

    /// <summary>Surfaces a failure from a navigation started by the chrome rather than a command.</summary>
    private void Observe(Task task) =>
        _ = task.ContinueWith(
            t => ((IMessageSink)this).Error(t.Exception!.GetBaseException().Message),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.FromCurrentSynchronizationContext());

    /// <summary>
    /// Minimise, maximise and close, at the right end of the tab row.
    /// </summary>
    /// <remarks>
    /// Drawn as text rather than as glyphs from a symbol font: the Segoe icon fonts are not
    /// on Linux or macOS, and a tofu box where the close button should be is worse
    /// everywhere than a plain character.
    /// </remarks>
    private Control BuildWindowButtons()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        row.Children.Add(WindowButton("\u2013", "Minimise", () => WindowState = WindowState.Minimized));

        row.Children.Add(WindowButton("\u25a1", "Maximise", () =>
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized));

        row.Children.Add(WindowButton("\u00d7", "Close", Close, closing: true));

        return row;
    }

    private Control WindowButton(string glyph, string tooltip, Action action, bool closing = false)
    {
        var label = new TextBlock
        {
            Text = glyph,
            FontFamily = _theme.Typeface.FontFamily,
            FontSize = _theme.FontSize,
            Foreground = _theme.MutedText,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var button = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(_theme.LeftPadding + 4, _theme.RowPadding),
            Child = label,
            Cursor = new Cursor(StandardCursorType.Arrow),
        };

        ToolTip.SetTip(button, tooltip);

        // Red on the close button only, and only under the pointer: a permanently red x in
        // the corner of a file manager reads as an error rather than as a button.
        button.PointerEntered += (_, _) =>
        {
            button.Background = closing ? _theme.BrokenText : _theme.CursorBackground;
            label.Foreground = _theme.FileText;
        };

        button.PointerExited += (_, _) =>
        {
            button.Background = Brushes.Transparent;
            label.Foreground = _theme.MutedText;
        };

        button.PointerPressed += (_, e) =>
        {
            action();

            // Or the press falls through to the window and starts dragging it instead.
            e.Handled = true;
        };

        return button;
    }

    /// <summary>
    /// Closes a tab by position, refusing to close the last one.
    /// </summary>
    /// <remarks>
    /// The rule <c>:tab-close</c> already follows: a window with no tabs has nothing to
    /// show, and quitting should be a decision rather than the end of a run of clicks.
    /// </remarks>
    private void CloseTabAt(int index)
    {
        if (_workspace.Tabs.Count <= 1)
        {
            ((IMessageSink)this).Info("Last tab; use :quit to close Fjord.");
            return;
        }

        _workspace.CloseTab(index);
        _list.Focus();
    }

    /// <summary>What the strip currently reads, so it is only rebuilt when that changes.</summary>
    private string[] _tabLabels = [];

    private bool TabLabelsChanged()
    {
        var tabs = _workspace.Tabs;

        if (_tabLabels.Length != tabs.Count)
        {
            return true;
        }

        for (var i = 0; i < tabs.Count; i++)
        {
            if (!string.Equals(_tabLabels[i], tabs[i].Title, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Rebuilds the tab strip. Cheap enough to redo whenever the set changes.</summary>
    private void RebuildTabBar()
    {
        // Always visible now: the row carries the window buttons, so it is the title bar.
        _tabStrip.Children.Clear();
        _tabLabels = [.. _workspace.Tabs.Select(t => t.Title)];

        for (var i = 0; i < _workspace.Tabs.Count; i++)
        {
            var isCurrent = i == _workspace.CurrentIndex;
            var index = i;

            var tab = _workspace.Tabs[i];

            var label = new TextBlock
            {
                // The lock goes before the number, so a column of tabs lines up on the
                // number whether or not any of them are locked.
                Text = $"{(tab.IsLocked ? "🔒 " : "")}{i + 1}: {tab.Title}",
                FontFamily = _theme.Typeface.FontFamily,
                FontSize = _theme.FontSize,
                Foreground = isCurrent ? _theme.FileText : _theme.MutedText,
            };

            var chip = new Border
            {
                Background = isCurrent ? _theme.CursorBackground : _theme.Stripe,
                Padding = new Thickness(_theme.LeftPadding, _theme.RowPadding),
                Margin = new Thickness(0, 0, 2, 0),
                Child = label,
                Cursor = new Cursor(StandardCursorType.Hand),
            };

            chip.PointerPressed += (_, e) =>
            {
                // Middle click closes, the way every browser has taught people it does.
                if (e.GetCurrentPoint(chip).Properties.IsMiddleButtonPressed)
                {
                    CloseTabAt(index);
                    e.Handled = true;
                    return;
                }

                _workspace.CurrentIndex = index;
                _dragFrom = index;
                _dragOrigin = e.GetPosition(_tabStrip);
                _list.Focus();
                e.Pointer.Capture(chip);

                // Or the window would start moving under a tab being dragged to reorder.
                e.Handled = true;
            };

            // Reordering is decided on release, from what is under the pointer then.
            // Reordering as the pointer moves would shuffle the strip under the cursor
            // mid-gesture and make the tab you are holding run away from you — so the
            // feedback moves instead, and the tabs stay put until you let go.
            chip.PointerMoved += (_, e) =>
            {
                if (_dragFrom is not { } from)
                {
                    return;
                }

                var at = e.GetPosition(_tabStrip);

                // A few pixels of slack, so a click with an unsteady hand is still a click
                // and not a drag that redraws the bar under it.
                if (!_dragging && Math.Abs(at.X - _dragOrigin.X) < DragThreshold)
                {
                    return;
                }

                _dragging = true;
                ShowDrag(from, DropTargetAt(at));
            };

            chip.PointerReleased += (_, e) =>
            {
                e.Pointer.Capture(null);

                if (_dragFrom is { } from)
                {
                    var dragged = _dragging;
                    _dragFrom = null;
                    _dragging = false;

                    if (dragged && DropTargetAt(e.GetPosition(_tabStrip)) is { } to && to != from)
                    {
                        _workspace.MoveTab(from, to);
                        return;
                    }

                    // A click rather than a drag, or dropped where it started: put the
                    // borrowed styling back, since nothing is going to rebuild the bar.
                    RebuildTabBar();
                }
            };

            _tabStrip.Children.Add(chip);
        }
    }

    /// <summary>
    /// Notes the active directory for <c>:recent</c>.
    /// </summary>
    /// <remarks>
    /// Skips the synthetic scrapbook listings, which are not places that can be navigated
    /// back to.
    /// </remarks>
    private void RecordVisit()
    {
        var directory = _workspace.Tabs.Count == 0 ? string.Empty : _workspace.ActivePane.Directory;

        if (directory.Length == 0 || directory.StartsWith("scrapbook:", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            _storage.RecordVisit(directory);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Recents are a convenience; losing one is not worth telling anyone about.
        }
    }

    private Control BuildStatusRow()
    {
        var row = new DockPanel();
        DockPanel.SetDock(_keys, Dock.Right);
        row.Children.Add(_keys);
        row.Children.Add(_status);
        return row;
    }

    private void ApplyTheme()
    {
        Background = _theme.Background;

        foreach (var bar in new[] { _pathBar, _statusBar })
        {
            bar.Background = _theme.Stripe;
            bar.Padding = new Thickness(_theme.LeftPadding, _theme.RowPadding);
        }

        _status.FontFamily = _theme.Typeface.FontFamily;
        _status.FontSize = _theme.FontSize;
        _status.Foreground = _theme.MutedText;

        // The breadcrumbs are rebuilt rather than restyled: they are a strip of controls
        // whose colours are baked in as they are created, and forcing a rebuild is one line
        // against a walk over children of three different kinds.
        _breadcrumbPath = string.Empty;

        _tabBar.Background = _theme.Background;
        _taskPanel.Background = _theme.Stripe;
        RebuildTabBar();

        _keys.FontFamily = _theme.Typeface.FontFamily;
        _keys.FontSize = _theme.FontSize;
        _keys.Foreground = _theme.CursorBar;
    }

    private void UpdateChrome()
    {
        var pane = _workspace.Tabs.Count == 0 ? null : _workspace.ActivePane;
        if (pane is null)
        {
            return;
        }

        RebuildBreadcrumbs(pane.Directory);

        // A tab's title comes from its pane's directory, which is empty until the first
        // listing arrives — so a bar built only when tabs are added or removed shows the
        // very first tab as "(empty)" for the life of the window. Compared rather than
        // rebuilt unconditionally, because this runs on every cursor move.
        if (TabLabelsChanged())
        {
            RebuildTabBar();
        }

        _input.SyncModeToPane(pane);

        // Pending keys and mode, exactly as QuteSharp's status bar shows them: a
        // half-typed "g" or an armed operator has to be visible or the app feels stuck.
        var mode = _input.Modes.Mode;
        var modeText = mode == KeyMode.Normal ? string.Empty : $"-- {mode.Name.ToUpperInvariant()} -- ";
        _keys.Text = modeText + _input.Modes.PendingKeys;
        UpdateKeyHints();

        var snapshot = pane.Snapshot;
        if (snapshot is null)
        {
            _status.Text = _commandUsage.Length > 0 ? _commandUsage : _message ?? string.Empty;
            return;
        }

        var view = snapshot.View;
        var position = view.Count == 0 ? "0" : (_list.CursorIndex + 1).ToString(CultureInfo.InvariantCulture);
        var selected = _list.Selection.Count > 0 ? $"  [{_list.Selection.Count} selected]" : string.Empty;
        // Says what it is counting. "loading…" on its own left the user with no idea
        // whether anything was happening or how much of it was left.
        var loading = snapshot.IsComplete
            ? string.Empty
            : $"  loading {view.Count:N0}…";
        var error = snapshot.Error is { } ex ? $"  [{ex.GetType().Name}: {ex.Message}]" : string.Empty;
        var message = _message is { } m ? $"  {Prefix(_messageLevel)}{m}" : string.Empty;
        var undo = _operations.NextUndoDescription is { } next ? $"  [u: {next}]" : string.Empty;

        // The usage of the command being typed wins while the line is open: it is the
        // thing the user is looking for an answer about right now.
        _status.Text = _commandUsage.Length > 0
            ? _commandUsage
            : $"{position}/{view.Count}{selected}{loading}{error}{message}{undo}";
        _status.Foreground = _messageLevel == MessageLevel.Error && _message is not null
            ? _theme.BrokenText
            : _theme.MutedText;
    }

    private static string Prefix(MessageLevel level) => level switch
    {
        MessageLevel.Error => "error: ",
        MessageLevel.Warning => "warning: ",
        _ => string.Empty,
    };

    private TextBlock Label(IBrush brush, string? text = null) => new()
    {
        Text = text,
        FontFamily = _theme.Typeface.FontFamily,
        FontSize = _theme.FontSize,
        Foreground = brush,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private Border Bar(Control content) => new()
    {
        Background = _theme.Stripe,
        Padding = new Thickness(_theme.LeftPadding, _theme.RowPadding),
        Child = content,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
}
