namespace Fjord.Core.Panes;

/// <summary>What the next keystroke names.</summary>
/// <remarks>
/// Only registers read a bare keystroke now. Quickmarks used to as well, and became words
/// typed on the command line instead: a name worth keeping for weeks is worth more than a
/// letter worth remembering for a minute.
/// </remarks>
public enum NameTarget
{
    /// <summary>The register the following command should use, as <c>"</c> does.</summary>
    Register,
}

/// <summary>
/// Mode and application actions a command may take.
/// </summary>
/// <remarks>
/// Entering visual mode and quitting need the mode manager and the desktop lifetime, both
/// of which live in the App layer. Routing them through an interface — the same shape as
/// <see cref="IPane"/> — keeps the commands themselves in <c>Fjord.Commands</c>, so they
/// appear in <c>:help</c>, stay bindable, and are covered by the test that resolves every
/// default binding against the registry.
/// <para>
/// Without this the mode commands would live in the App layer, invisible to that test,
/// and a renamed command could ship a key that silently does nothing.
/// </para>
/// </remarks>
public interface IModeHost
{
    /// <summary>Enters visual mode anchored at the cursor, or leaves it if already there.</summary>
    void ToggleVisual(IPane pane);

    /// <summary>Returns to normal mode, abandoning any pending operator or register.</summary>
    void LeaveMode(IPane pane);

    /// <summary>Opens the command line, pre-filled with <paramref name="initial"/>.</summary>
    void EnterCommandLine(string initial);

    /// <summary>
    /// Waits for one keystroke and treats it as a name rather than a binding.
    /// </summary>
    /// <remarks>
    /// How <c>"a</c>, <c>ma</c> and <c>'a</c> all work: the second key is data. Routing it
    /// through here rather than special-casing the three characters in the key handler is
    /// what keeps them rebindable — a user who wants <c>M</c> to set marks can say so.
    /// </remarks>
    void ReadName(NameTarget target);

    /// <summary>Directories remembered with <c>m</c>.</summary>
    Navigation.MarkFile Marks { get; }

    /// <summary>
    /// Puts a yes/no question in the status bar and runs the action if the answer is yes.
    /// </summary>
    /// <remarks>
    /// Answered with y or Enter; anything else declines. A modal dialog would be the
    /// obvious alternative and the wrong one: it moves focus, it needs the mouse to be
    /// comfortable, and it stops the next keystroke being part of the same sentence.
    /// </remarks>
    void Ask(string question, Action confirmed);

    /// <summary>Turns the render-time overlay on or off. Returns the new state.</summary>
    bool ToggleFrameStats();

    /// <summary>
    /// Turns mouse dragging of entries on or off. Returns the new state.
    /// </summary>
    /// <remarks>
    /// A mode rather than always-on, because the window has no title bar: the listing is
    /// most of what there is to grab, and it is what the user drags to move the window.
    /// The two gestures are the same gesture, so one of them has to be asked for.
    /// </remarks>
    bool ToggleDragMode();

    /// <summary>Closes the application.</summary>
    void Quit();
}
