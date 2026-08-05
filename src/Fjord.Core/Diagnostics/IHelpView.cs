namespace Fjord.Core.Diagnostics;

/// <summary>
/// Somewhere to show a page of text over the file list.
/// </summary>
/// <remarks>
/// <c>:help</c> is rendered in the app rather than opened in a browser or an editor.
/// For a keyboard-driven file manager the thing being looked up is almost always "what key
/// does this", and having to leave the app to find out is precisely the wrong answer.
/// </remarks>
public interface IHelpView
{
    bool IsVisible { get; }

    void Show(string title, string content);

    void Hide();
}
