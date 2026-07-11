using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using ShadowsMcp.Core.Mcp;
using ShadowsMcp.Core.Util;

namespace ShadowsMcp.Core.Http
{
    /// <summary>
    /// Minimal HTTP host for the MCP Streamable HTTP transport, plain-JSON response mode.
    /// POST /mcp only; GET on /mcp is 405 (we open no SSE stream); anything else 404.
    /// Uses HttpListener: under Unity's Mono this is a managed socket implementation,
    /// so no Windows URLACL/admin rights are needed, even for wildcard binds.
    /// </summary>
    public sealed class HttpTransport : ITransport
    {
        private const int MaxBodyBytes = 4 * 1024 * 1024;
        private const int PortRetries = 10;
        public const string McpPath = "/mcp";

        private readonly McpServer _server;
        private readonly bool _listenLan;
        private readonly int _basePort;

        private HttpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;

        public int BoundPort { get; private set; }

        /// <param name="listenLan">true → bind all interfaces; false → loopback only.</param>
        public HttpTransport(McpServer server, bool listenLan, int port)
        {
            _server = server;
            _listenLan = listenLan;
            _basePort = port;
        }

        public int Start()
        {
            Exception lastError = null;
            for (int i = 0; i < PortRetries; i++)
            {
                int port = _basePort + i;
                string prefix = (_listenLan ? "http://*:" : "http://127.0.0.1:") + port + "/";
                var listener = new HttpListener();
                listener.Prefixes.Add(prefix);
                try
                {
                    listener.Start();
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    try { listener.Close(); } catch { }
                    continue;
                }
                _listener = listener;
                BoundPort = port;
                _running = true;
                _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "ShadowsMCP-http" };
                _acceptThread.Start();
                Log.Info("listening on " + prefix + "mcp" + (_listenLan ? " (all interfaces)" : " (loopback only)"));
                return port;
            }
            throw new InvalidOperationException(
                "could not bind any port in " + _basePort + "-" + (_basePort + PortRetries - 1), lastError);
        }

        public void Stop()
        {
            _running = false;
            try { if (_listener != null) _listener.Close(); } catch { }
            _listener = null;
            Log.Info("server stopped");
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = _listener.GetContext();
                }
                catch
                {
                    if (_running) Thread.Sleep(50);
                    continue; // listener closed or transient accept failure
                }
                // Handle on the thread pool so a long tools/call doesn't block ping/initialize.
                ThreadPool.QueueUserWorkItem(delegate { HandleRequestSafe(ctx); });
            }
        }

        private void HandleRequestSafe(HttpListenerContext ctx)
        {
            try
            {
                HandleRequest(ctx);
            }
            catch (Exception ex)
            {
                Log.Error("request handling failed", ex);
                try { ctx.Response.Abort(); } catch { }
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            HttpListenerRequest req = ctx.Request;
            HttpListenerResponse res = ctx.Response;

            string path = req.Url != null ? req.Url.AbsolutePath : "";
            if (path.EndsWith("/") && path.Length > 1) path = path.Substring(0, path.Length - 1);

            if (!string.Equals(path, McpPath, StringComparison.OrdinalIgnoreCase))
            {
                WriteText(res, 404, "not found — MCP endpoint is POST " + McpPath);
                return;
            }
            if (req.HttpMethod != "POST")
            {
                res.AddHeader("Allow", "POST");
                WriteText(res, 405, "method not allowed — use POST (this server does not offer an SSE stream)");
                return;
            }
            if (req.ContentLength64 > MaxBodyBytes)
            {
                WriteText(res, 413, "request body too large");
                return;
            }

            string body;
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
            {
                body = reader.ReadToEnd();
            }

            int status;
            string responseBody = _server.HandlePost(body, out status);

            res.StatusCode = status;
            if (responseBody == null)
            {
                res.ContentLength64 = 0;
                res.OutputStream.Close();
            }
            else
            {
                byte[] bytes = Encoding.UTF8.GetBytes(responseBody);
                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = bytes.Length;
                res.OutputStream.Write(bytes, 0, bytes.Length);
                res.OutputStream.Close();
            }
        }

        private static void WriteText(HttpListenerResponse res, int status, string message)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            res.StatusCode = status;
            res.ContentType = "text/plain; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.OutputStream.Close();
        }
    }
}
