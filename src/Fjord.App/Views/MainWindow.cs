using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Fjord.Commands;
using Fjord.Config;
using Fjord.Core.Diagnostics;
using Fjord.Core.FileSystem;
using Fjord.Core.Input;

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
    private readonly PaneController _pane;
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
    private readonly ConfigWatcher? _watcher;

    private FjordConfig _config;
    private string? _message;
    private MessageLevel _messageLevel;

    public MainWindow(FjordPaths paths, ConfigLoadResult initial, IDirectoryLister lister)
    {
        _paths = paths;
        _config = initial.Config;
        _message = initial.Error;
        _messageLevel = initial.Error is null ? MessageLevel.Info : MessageLevel.Error;

        Title = "Fjord";
        Width = 1000;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _theme = new ListTheme(_config.Appearance);
        _list = new FileListView(_theme);
        _pane = new PaneController(lister, _list, _config.ToSortSpec(), _config.ToFilterSpec());

        _bindings = new KeyBindings();
        _modeRegistry = new KeyModeRegistry();

        var registry = new CommandRegistry();
        var navigation = new NavigationCommands();
        registry.Register(navigation);

        // The mode manager asks the registry whether a command is an operator, and the
        // mode commands ask the input controller to change mode — so the two are built
        // with a late-bound reference to each other.
        InputController? controller = null;
        var modes = new ModeManager(_bindings, _modeRegistry, IsOperator);
        var parser = new CommandParser(registry);

        controller = new InputController(
            modes, _modeRegistry, parser, new OperatorDispatcher(parser, registry), this, () => _pane);
        _input = controller;

        registry.Register(new ModeCommands(controller));

        _commandLine = new CommandLine(
            _theme, new CompletionEngine(registry, new PathCompletionSource(() => _pane)));

        DefaultBindings.Load(_bindings, _modeRegistry);
        ApplyUserBindings();

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

        DockPanel.SetDock(_pathBar, Dock.Top);
        DockPanel.SetDock(_statusBar, Dock.Bottom);
        DockPanel.SetDock(_commandLine, Dock.Bottom);
        DockPanel.SetDock(_commandLine.CompletionHost, Dock.Bottom);

        // Order matters: the completion list sits above the command line, which sits above
        // the status bar, and the file list fills what is left.
        Content = new DockPanel
        {
            Children = { _pathBar, _statusBar, _commandLine, _commandLine.CompletionHost, scroller },
        };

        ApplyTheme();

        // Tunnelling, so the mode manager sees a key before whatever has focus does. This
        // is what will let Escape leave the command line once it has a TextBox.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        _pane.Changed += (_, _) => UpdateChrome();
        _input.Handled += (_, _) => UpdateChrome();
        _input.CommandLineRequested += (_, initial) => _commandLine.Open(initial);

        _commandLine.Accepted += (_, line) =>
        {
            _list.Focus();
            _input.Execute(line);
        };

        _commandLine.Cancelled += (_, _) => _list.Focus();

        try
        {
            _watcher = new ConfigWatcher(_paths.ConfigFile, ReloadConfig, a => Dispatcher.UIThread.Post(a));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Hot reload is a convenience; losing it must not stop the app starting.
            _message ??= $"config watch unavailable: {ex.Message}";
        }

        Closed += (_, _) => _watcher?.Dispose();

        Opened += async (_, _) =>
        {
            ClampToScreen();
            _list.Focus();
            await _pane.NavigateAsync(Environment.CurrentDirectory);
        };

        bool IsOperator(string name) => registry.Find(name)?.IsOperator == true;
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
    private void ReloadConfig()
    {
        var result = ConfigFile.Load(_paths.ConfigFile);

        if (!result.Succeeded)
        {
            _message = result.Error;
            _messageLevel = MessageLevel.Error;
            UpdateChrome();
            return;
        }

        _config = result.Config;
        _message = null;

        _theme.Apply(_config.Appearance);
        ApplyTheme();
        ApplyUserBindings();

        // Reorders what is already loaded; no directory is re-read.
        _pane.SetOrder(_config.ToSortSpec(), _config.ToFilterSpec());

        _list.RefreshTheme();
        UpdateChrome();
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

        _keys.FontFamily = _theme.Typeface.FontFamily;
        _keys.FontSize = _theme.FontSize;
        _keys.Foreground = _theme.CursorBar;
    }

    private void UpdateChrome()
    {
        _path.Text = _pane.Directory;

        // Pending keys and mode, exactly as QuteSharp's status bar shows them: a
        // half-typed "g" or an armed operator has to be visible or the app feels stuck.
        var mode = _input.Modes.Mode;
        var modeText = mode == KeyMode.Normal ? string.Empty : $"-- {mode.Name.ToUpperInvariant()} -- ";
        _keys.Text = modeText + _input.Modes.PendingKeys;

        var snapshot = _pane.Snapshot;
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

        _status.Text = $"{position}/{view.Count}{selected}{loading}{error}{message}";
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
