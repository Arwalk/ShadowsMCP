namespace ShadowsMcp.Core.Http
{
    /// <summary>
    /// Transport abstraction so the HTTP layer can be swapped (HttpListener today;
    /// a raw TcpListener implementation if HttpListener misbehaves under the game's Mono).
    /// </summary>
    public interface ITransport
    {
        /// <summary>Start listening. Returns the port actually bound (may differ on conflict retry).</summary>
        int Start();
        void Stop();
    }
}
