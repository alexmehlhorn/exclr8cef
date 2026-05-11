using Exclr8Cef.Native;

namespace Exclr8Cef;

/// <summary>
/// Isolated request context — its own cookie jar, cache, and per-origin
/// storage. Browsers created in different request contexts can't see each
/// other's cookies / localStorage / IndexedDB / cache; useful for
/// multi-profile, "container tab" / incognito-like flows.
///
/// Created via <see cref="Cef.CreateRequestContext"/>. Disposing drops
/// the shim's reference; CEF keeps the underlying context alive while
/// any browser is using it, then tears it down once the last browser
/// closes.
///
/// In-memory (incognito) contexts vanish entirely when the last browser
/// closes; on-disk contexts persist to <c>CachePath</c> across runs.
/// </summary>
public sealed class CefRequestContext : IDisposable
{
    private int _handle;

    internal int Handle => _handle;
    public bool IsClosed => _handle == 0;

    internal CefRequestContext(int handle) { _handle = handle; }

    /// <summary>
    /// Drop the shim's reference to this context. Outstanding browsers
    /// using it keep it alive until they close. Idempotent.
    /// </summary>
    public void Dispose()
    {
        int h = System.Threading.Interlocked.Exchange(ref _handle, 0);
        if (h != 0) Excef.excef_release_request_context(h);
    }
}
