// Minimal static server for the UI prototype: node serve.mjs [port]
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
const files = new Map([
  ['/', ['index.html', 'text/html']],
  ['/index.html', ['index.html', 'text/html']],
  ['/style.css', ['style.css', 'text/css']],
  ['/app.js', ['app.js', 'text/javascript']],
]);
const port = Number(process.argv[2] || 5179);
if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('端口应为 1 到 65535。');

createServer(async (req, res) => {
  if (req.method !== 'GET' && req.method !== 'HEAD') { res.writeHead(405).end(); return; }
  const entry = files.get(req.url.split('?')[0]);
  if (!entry) { res.writeHead(404).end(); return; }
  try {
    const [file, mime] = entry;
    const body = await readFile(new URL(file, import.meta.url));
    res.writeHead(200, { 'content-type': mime + '; charset=utf-8', 'cache-control': 'no-store' })
      .end(req.method === 'HEAD' ? undefined : body);
  } catch { res.writeHead(404).end('not found'); }
}).listen(port, '127.0.0.1', () => console.log(`prototype: http://127.0.0.1:${port}/`));
