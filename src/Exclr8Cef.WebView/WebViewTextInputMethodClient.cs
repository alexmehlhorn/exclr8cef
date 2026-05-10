using Avalonia;
using Avalonia.Input.TextInput;

namespace Exclr8Cef.WebView;

/// <summary>
/// Bridges Avalonia's IME (composition / preedit) into CEF's IME entry
/// points. Attached to the platform's TextInputMethod when the WebView
/// gains focus.
/// </summary>
internal sealed class WebViewTextInputMethodClient : TextInputMethodClient
{
    private readonly WebView _owner;

    public WebViewTextInputMethodClient(WebView owner)
    {
        _owner = owner;
    }

    public override Visual TextViewVisual => _owner;

    public override bool SupportsPreedit => true;

    public override bool SupportsSurroundingText => false;

    public override string SurroundingText => string.Empty;

    public override Rect CursorRectangle
    {
        // We don't know where the page caret is without JS, so put the IME
        // candidate window at the bottom-left of the WebView. Avalonia's
        // platform IME will translate to screen coords.
        get => new Rect(0, _owner.Bounds.Height - 24, 1, 22);
    }

    public override TextSelection Selection { get; set; } = new TextSelection(0, 0);

    public override void SetPreeditText(string? preeditText)
    {
        var id = _owner.BrowserId;
        if (id <= 0) return;

        if (string.IsNullOrEmpty(preeditText))
        {
            Cef.ImeFinishComposing(id);
        }
        else
        {
            Cef.ImeSetComposition(id, preeditText,
                replacementRangeStart: 0,
                replacementRangeLength: 0,
                selectionStart: 0,
                selectionLength: preeditText.Length);
        }
    }
}
