#!/usr/bin/env node
// Transparent logging proxy for the HTTP MCP server: forwards every call to the
// real server unchanged and tees each request/response exchange (with byte sizes)
// to a JSONL file — for analysing response sizes / token cost of a real session.
//
// Run it on the machine where the MCP client (e.g. Claude Code) runs, then point
// the client at the proxy instead of the game:
//   node tools/mcp-tee.mjs 9017 http://<GAME-PC-IP>:8017/mcp game-log.jsonl
//   claude mcp remove shadows
//   claude mcp add --transport http shadows http://localhost:9017/mcp
// Summarise the capture afterwards with tools/mcp-log-report.sh.
//
// Usage: node tools/mcp-tee.mjs [listenPort] [upstreamUrl] [logFile]
//   defaults: 9017  http://127.0.0.1:8017/mcp  mcp-log.jsonl
//
// Caveat: buffers each response, which fits this server's plain-JSON response
// mode. If a server ever streams SSE (text/event-stream), use mitmproxy instead.

import http from 'node:http';
import { appendFileSync } from 'node:fs';

const LISTEN   = Number(process.argv[2] || 9017);
const UPSTREAM = new URL(process.argv[3] || 'http://127.0.0.1:8017/mcp');
const LOG      = process.argv[4] || 'mcp-log.jsonl';

const readBody = (stream) => new Promise((resolve) => {
  const chunks = [];
  stream.on('data', (c) => chunks.push(c));
  stream.on('end', () => resolve(Buffer.concat(chunks)));
});

const asJson = (buf) => {
  const s = buf.toString('utf8');
  if (!s) return null;
  try { return JSON.parse(s); } catch { return s; } // non-JSON kept as raw text
};

const server = http.createServer(async (cReq, cRes) => {
  const reqBody = await readBody(cReq);

  // Forward method + headers verbatim (incl. Mcp-Session-Id / MCP-Protocol-Version);
  // strip accept-encoding so the upstream reply is uncompressed and logs cleanly.
  const headers = { ...cReq.headers, host: UPSTREAM.host };
  delete headers['accept-encoding'];

  const uReq = http.request({
    protocol: UPSTREAM.protocol,
    hostname: UPSTREAM.hostname,
    port: UPSTREAM.port,
    method: cReq.method,
    path: cReq.url,            // the client is configured with the /mcp endpoint
    headers,
  }, (uRes) => {
    readBody(uRes).then((resBody) => {
      const rec = {
        t: new Date().toISOString(),
        method: cReq.method,
        status: uRes.statusCode,
        reqBytes: reqBody.length,
        resBytes: resBody.length,          // <- the number to watch for token cost
        request: asJson(reqBody),
        response: asJson(resBody),
      };
      try { appendFileSync(LOG, JSON.stringify(rec) + '\n'); } catch { /* logging is best-effort */ }
      cRes.writeHead(uRes.statusCode, uRes.headers);
      cRes.end(resBody);
    });
  });
  uReq.on('error', (e) => {
    cRes.writeHead(502, { 'content-type': 'text/plain' });
    cRes.end('mcp-tee upstream error: ' + e.message);
  });
  uReq.end(reqBody);
});

server.listen(LISTEN, () => {
  console.error(`mcp-tee: http://localhost:${LISTEN}  ->  ${UPSTREAM.href}   logging -> ${LOG}`);
});
