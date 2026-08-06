namespace Fjord.Core.Panes;

/// <summary>What the next keystroke names.</summary>
public enum NameTarget
{
    /// <summary>The register the following command should use, as <c>"</c> does.</summary>
    Register,

    /// <summary>The mark to set here, as <c>m</c> does.</summary>
    MarkToSet,

    /// <summary>The mark to jump to, as <c>'</c> does.</summary>
    MarkToVisit,
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

    /// <summary>Turns the render-time overlay on or off. Returns the new state.</summary>
    bool ToggleFrameStats();

    /// <summary>Closes the application.</summary>
    void Quit();
}
