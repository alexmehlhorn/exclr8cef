using System.Text.Json;

namespace Exclr8Cef.Cdp;

/// <summary>
/// Helpers for the JSON shape CDP uses. Server events look like
/// <c>{"method":"Domain.event","params":{…}}</c>; replies look like
/// <c>{"id":N,"result":{…}}</c>. The domain clients use these to peel
/// off the method name without a full parse, then parse params only if
/// the event is one they actually care about.
/// </summary>
internal static class CdpJson
{
    /// <summary>
    /// Extract the <c>method</c> field from a CDP event JSON string
    /// without a full parse. Returns null if absent.
    /// </summary>
    public static string? GetEventMethod(string json)
    {
        const string Marker = "\"method\":\"";
        int idx = json.IndexOf(Marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        idx += Marker.Length;
        int end = json.IndexOf('"', idx);
        return end < 0 ? null : json.Substring(idx, end - idx);
    }

    /// <summary>
    /// Parse a CDP event JSON and return its <c>params</c> element.
    /// Throws if <c>params</c> is absent — callers should only invoke
    /// this after <see cref="GetEventMethod"/> matched a known method
    /// for which params is guaranteed.
    /// </summary>
    public static JsonElement ParseEventParams(string json)
    {
        using var doc = JsonDocument.Parse(json);
        // Clone so the JsonDocument can be disposed; caller keeps the element.
        return doc.RootElement.GetProperty("params").Clone();
    }

    /// <summary>
    /// Parse a CDP reply JSON and return its <c>result</c> element. The
    /// caller owns the returned element (cloned, so the inner
    /// JsonDocument is disposed before return).
    /// </summary>
    public static JsonElement ParseResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("result").Clone();
    }
}
