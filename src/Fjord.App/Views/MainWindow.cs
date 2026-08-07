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
    private readonly TextBlock _path;
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
    private readonly CompletionList _keyHints;
    private readonly DispatcherTimer _keyHintDelay;
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
        _tabBar = new Border
        {
            Background = _theme.Background,
            Child = _tabStrip,

            // Hidden with a single tab: a one-tab tab bar is chrome that says nothing.
            IsVisible = false,
        };

        _keyHints = new CompletionList(_theme);

        // Delayed, or every "gg" typed at speed flashes a panel on the way past. Long
        // enough that a user who knows the binding never sees it, short enough that one
        // who has stopped to think does. Which-key picked 300 ms and it is about right.
        _keyHintDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _keyHintDelay.Tick += (_, _) =>
        {
            _keyHintDelay.Stop();
            ShowKeyHints();
        };

        _path = Label(_theme.FileText);
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
        DockPanel.SetDock(_commandLine, Dock.Bottom);
        DockPanel.SetDock(_commandLine.CompletionHost, Dock.Bottom);
        DockPanel.SetDock(_taskPanel, Dock.Bottom);
        DockPanel.SetDock(_keyHints, Dock.Bottom);

        // Order matters: the completion list sits above the command line, which sits above
        // the status bar, and the file list fills what is left.
        Content = new DockPanel
        {
            Children =
            {
                _tabBar, _pathBar, _statusBar, _commandLine, _commandLine.CompletionHost,
                _keyHints, _taskPanel, new Panel { Children = { scroller, _help } },
            },
        };

        ApplyTheme();

        // Tunnelling, so the mode manager sees a key before whatever has focus does. This
        // is what will let Escape leave the command line once it has a TextBox.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        _workspace.Changed += (_, _) =>
        {
            RebuildTabBar();
            UpdateChrome();
            RecordVisit();
        };
        _input.Handled += (_, _) => UpdateChrome();
        _input.CommandLineRequested += (_, initial) => _commandLine.Open(initial);

        _input.FrameStatsToggled += (_, _) =>
        {
            _list.ShowFrameStats = !_list.ShowFrameStats;
            _list.InvalidateVisual();
        };
        _input.FrameStatsEnabled = () => _list.ShowFrameStats;

        _commandLine.Accepted += (_, line) =>
        {
            _list.Focus();
            _input.Execute(line);
        };

        _commandLine.Cancelled += (_, _) => _list.Focus();

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
                pane.Changed += (_, _) => UpdateChrome();

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
        if (_commandLine.IsOpen)
        {
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
            tab.Active.SetOrder(_config.ToSortSpec(), _config.ToFilterSpec());
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

    /// <summary>Rebuilds the tab strip. Cheap enough to redo whenever the set changes.</summary>
    /// <summary>
    /// Shows what could follow a half-typed binding.
    /// </summary>
    /// <remarks>
    /// The gap this closes: <c>y</c> alone does nothing visible and gives no clue that
    /// <c>yy</c> exists, so a binding you have not memorised is a binding you cannot find
    /// from the keyboard. Also covers operator-pending, where the useful answer is the list
    /// of motions the operator will accept.
    /// </remarks>
    private void UpdateKeyHints()
    {
        var buffered = _input.Modes.Buffered;
        var pendingOperator = _input.Modes.PendingOperator is not null;

        if (buffered.Count == 0 && !pendingOperator)
        {
            _keyHintDelay.Stop();
            _keyHints.Hide();
            return;
        }

        // Restarted on every keystroke, so the delay measures how long the user has been
        // stuck rather than how long the sequence has been going.
        _keyHintDelay.Stop();
        _keyHintDelay.Start();
    }

    private void ShowKeyHints()
    {
        var mode = _input.Modes.Mode;
        var buffered = _input.Modes.Buffered;

        var items = _bindings.Continuations(mode, buffered)
            .Select(c => new CompletionItem(
                c.Keys.ToString(),
                DescribeBinding(c.Command),
                Group(c.Command)))
            .OrderBy(i => i.Group, StringComparer.Ordinal)
            .ToList();

        if (items.Count == 0)
        {
            _keyHints.Hide();
            return;
        }

        _keyHints.Show(items);
    }

    /// <summary>What a binding does, in the registry's own words where it has any.</summary>
    private string DescribeBinding(string command)
    {
        var name = (command.StartsWith(':') ? command[1..] : command).Split(' ')[0];
        return _registry.Find(name)?.Description ?? command;
    }

    private string Group(string command)
    {
        var name = (command.StartsWith(':') ? command[1..] : command).Split(' ')[0];
        return _registry.Find(name)?.Group ?? string.Empty;
    }

    private void RebuildTabBar()
    {
        _tabBar.IsVisible = _workspace.Tabs.Count > 1;
        _tabStrip.Children.Clear();

        if (!_tabBar.IsVisible)
        {
            return;
        }

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

            chip.PointerPressed += (_, _) =>
            {
                _workspace.CurrentIndex = index;
                _list.Focus();
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

        foreach (var (bar, label, brush) in new[]
                 {
                     (_pathBar, _path, _theme.FileText),
                     (_statusBar, _status, _theme.MutedText),
                 })
        {
            bar.Background = _theme.Stripe;
            bar.Padding = new Thickness(_theme.LeftPadding, _theme.RowPadding);
            label.FontFamily = _theme.Typeface.FontFamily;
            label.FontSize = _theme.FontSize;
            label.Foreground = brush;
        }

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

        _path.Text = pane.Directory;

        // Pending keys and mode, exactly as QuteSharp's status bar shows them: a
        // half-typed "g" or an armed operator has to be visible or the app feels stuck.
        var mode = _input.Modes.Mode;
        var modeText = mode == KeyMode.Normal ? string.Empty : $"-- {mode.Name.ToUpperInvariant()} -- ";
        _keys.Text = modeText + _input.Modes.PendingKeys;
        UpdateKeyHints();

        var snapshot = pane.Snapshot;
        if (snapshot is null)
        {
            _status.Text = _message ?? string.Empty;
            return;
        }

        var view = snapshot.View;
        var position = view.Count == 0 ? "0" : (_list.CursorIndex + 1).ToString(CultureInfo.InvariantCulture);
        var selected = _list.Selection.Count > 0 ? $"  [{_list.Selection.Count} selected]" : string.Empty;
        var loading = snapshot.IsComplete ? string.Empty : "  loading…";
        var error = snapshot.Error is { } ex ? $"  [{ex.GetType().Name}: {ex.Message}]" : string.Empty;
        var message = _message is { } m ? $"  {Prefix(_messageLevel)}{m}" : string.Empty;
        var undo = _operations.NextUndoDescription is { } next ? $"  [u: {next}]" : string.Empty;

        _status.Text = $"{position}/{view.Count}{selected}{loading}{error}{message}{undo}";
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

    private TextBlock Label(IBrush brush) => new()
    {
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
