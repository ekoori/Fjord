namespace Fjord.Core.Platform;

/// <summary>Why registering as the folder handler could not be done.</summary>
public enum HandlerOutcome
{
    /// <summary>It worked.</summary>
    Done,

    /// <summary>The system was written to, but the change only takes effect on next login.</summary>
    NeedsRestart,

    /// <summary>Refused for want of privileges. Worth retrying elevated.</summary>
    NeedsElevation,

    /// <summary>This platform has no user-level way to do it at all.</summary>
    Unsupported,
}

/// <summary>The result of asking, and what to tell the user.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Detail">A sentence for the status line. Never empty.</param>
public readonly record struct HandlerResult(HandlerOutcome Outcome, string Detail)
{
    public bool Succeeded => Outcome is HandlerOutcome.Done or HandlerOutcome.NeedsRestart;
}

/// <summary>
/// Registering Fjord as the thing that opens folders, for this user.
/// </summary>
/// <remarks>
/// Current user only, deliberately. A machine-wide association needs administrator rights,
/// changes what every account on the computer does, and is a decision one user should not
/// be making on another's behalf. On Windows the user-level keys are also the ones the
/// shell consults first, so this is both the safer and the more effective place to write.
/// <para>
/// Separate from <see cref="IPlatformServices"/> because it is the one part of the platform
/// seam that <i>writes</i> to the system rather than asking it something, and the one that
/// can fail for want of privilege. Keeping it apart means the interface that every command
/// touches stays free of that.
/// </para>
/// </remarks>
public interface IFolderHandler
{
    /// <summary>Whether this platform can be asked at all.</summary>
    bool IsSupported { get; }

    /// <summary>Whether Fjord is currently what opens folders for this user.</summary>
    bool IsDefault();

    /// <summary>Makes Fjord the folder handler for this user.</summary>
    /// <param name="executable">The path to register. The running program, normally.</param>
    HandlerResult Register(string executable);

    /// <summary>Puts back whatever opened folders before.</summary>
    HandlerResult Unregister();
}
