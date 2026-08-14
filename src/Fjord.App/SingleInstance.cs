using System.IO.Pipes;

namespace Fjord.App;

/// <summary>
/// One window per user: a second launch hands its paths to the running Fjord and exits.
/// </summary>
/// <remarks>
/// Adapted from noPDF's <c>SingleInstance</c>, which solves the same problem for a viewer.
/// See VENDORED.md.
/// <para>
/// The reason Fjord can be a file manager at all. Double-clicking folders is how a desktop
/// asks a file manager to show something, and each one starts a new process — without this,
/// opening five folders means five windows, which is exactly what a tabbed file manager is
/// for avoiding.
/// </para>
/// <para>
/// A named pipe rather than a mutex: a mutex answers "is one already running" but gives
/// nowhere to send the path, and the path is the whole message.
/// </para>
/// </remarks>
public static class SingleInstance
{
    /// <summary>Set to anything to run an isolated instance beside a real one.</summary>
    public const string DisableVariable = "FJORD_NO_SINGLE_INSTANCE";

    /// <summary>
    /// Per user, so two people on one machine get a window each.
    /// </summary>
    /// <remarks>
    /// Not per session. Two logins by the same user — a remote desktop beside a local one —
    /// share the name, and the second launch forwards to the window on the other desktop
    /// where it cannot be seen. Rare enough to accept, and the escape hatch covers it.
    /// </remarks>
    private static readonly string PipeName = "Fjord.SingleInstance." + Environment.UserName;

    /// <summary>How long to wait for a running instance before assuming there is none.</summary>
    /// <remarks>
    /// Long enough to cross a busy machine, short enough that a cold start is not visibly
    /// delayed by asking. Every launch pays this when nothing is listening.
    /// </remarks>
    private const int ConnectTimeoutMs = 300;

    /// <summary>Whether the escape hatch is set.</summary>
    public static bool IsDisabled =>
        Environment.GetEnvironmentVariable(DisableVariable) is { Length: > 0 };

    /// <summary>
    /// Hands <paramref name="paths"/> to a running instance.
    /// </summary>
    /// <returns>True when they were delivered, and the caller should exit.</returns>
    public static bool TryForward(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (IsDisabled)
        {
            return false;
        }

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(ConnectTimeoutMs);

            using var writer = new StreamWriter(client) { AutoFlush = true };

            foreach (var path in paths)
            {
                writer.WriteLine(path);
            }

            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // Nothing listening, or a pipe we are not allowed to talk to. Either way this
            // process becomes the window.
            return false;
        }
    }

    /// <summary>
    /// Listens for paths forwarded by later launches.
    /// </summary>
    /// <param name="onPaths">
    /// Called from a background thread. The handler marshals to the UI itself.
    /// </param>
    /// <remarks>
    /// One connection at a time, served in a loop. A file manager is asked to open
    /// something a few times a minute at most, and a queue of one keeps the whole thing to
    /// a single thread that spends its life blocked.
    /// </remarks>
    public static void Listen(Action<IReadOnlyList<string>> onPaths)
    {
        ArgumentNullException.ThrowIfNull(onPaths);

        if (IsDisabled)
        {
            return;
        }

        var thread = new Thread(() => Serve(onPaths))
        {
            IsBackground = true,
            Name = "fjord-single-instance",
        };

        thread.Start();
    }

    private static void Serve(Action<IReadOnlyList<string>> onPaths)
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);

                server.WaitForConnection();

                using var reader = new StreamReader(server);

                var paths = reader.ReadToEnd()
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.TrimEnd('\r'))
                    .Where(line => line.Length > 0)
                    .ToList();

                if (paths.Count > 0)
                {
                    onPaths(paths);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A half-open connection or a transient refusal. Pausing keeps a repeating
                // failure from becoming a spin, and the loop keeps the window reachable.
                Thread.Sleep(200);
            }
        }
    }
}
