using System;
using System.Threading;
using ShadowsMcp.Core.Http;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;
using ShadowsMcp.Core.Util;

namespace ShadowsMcp.TestHost
{
    /// <summary>
    /// Runs the mod's MCP protocol stack on Linux with fake tools, so the whole
    /// HTTP/JSON-RPC/MCP layer can be exercised with curl without the game.
    /// Usage: ShadowsMcp.TestHost [port]
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            int port = 8017;
            if (args.Length > 0 && !int.TryParse(args[0], out port))
            {
                Console.Error.WriteLine("usage: ShadowsMcp.TestHost [port]");
                return 2;
            }

            Log.Sink = Console.WriteLine;

            var host = new ToolHostBase();
            host.Register(new ToolDefinition(
                "echo",
                "Echoes back the provided text.",
                Schema.Object(Schema.Prop("text", Schema.String("Text to echo"), required: true)),
                a => ToolResult.Ok("echo: " + a["text"].AsString(""))));
            host.Register(new ToolDefinition(
                "fake_overview",
                "Returns a fake game overview (structure mirrors the real game_overview tool).",
                Schema.Object(),
                a => ToolResult.Ok(JsonValue.NewObject()
                    .Set("turn", 42)
                    .Set("god", "The Test Entity")
                    .Set("counts", JsonValue.NewObject()
                        .Set("locations", 100)
                        .Set("units", 25)))));
            host.Register(new ToolDefinition(
                "fail_tool",
                "Always fails, to exercise the isError path.",
                Schema.Object(),
                a => ToolResult.Error("this tool always fails (as designed)")));
            host.Register(new ToolDefinition(
                "slow_tool",
                "Sleeps the given number of milliseconds, to exercise concurrent requests.",
                Schema.Object(Schema.Prop("ms", Schema.Integer("Milliseconds to sleep"))),
                a => { Thread.Sleep(a["ms"].AsInt(1000)); return ToolResult.Ok("slept"); }));

            var server = new McpServer(host, "shadows-mcp-testhost", "0.1.0");
            var transport = new HttpTransport(server, listenLan: false, port: port);
            int bound = transport.Start();
            Console.WriteLine("READY on http://127.0.0.1:" + bound + "/mcp");

            // Run until killed (SIGTERM/Ctrl-C); background thread does the work.
            var forever = new ManualResetEvent(false);
            Console.CancelKeyPress += delegate { transport.Stop(); forever.Set(); };
            forever.WaitOne();
            return 0;
        }
    }
}
