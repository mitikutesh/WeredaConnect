/**
 * Simple SPA-aware static file server for Flutter web builds.
 * Falls back to index.html for any path that doesn't match a real file.
 *
 * Usage: node spa-server.cjs <directory> <port>
 */
const http = require("http");
const fs = require("fs");
const path = require("path");

const dir = process.argv[2] || ".";
const port = parseInt(process.argv[3] || "8080", 10);

const mimeTypes = {
  ".html": "text/html",
  ".js": "application/javascript",
  ".css": "text/css",
  ".json": "application/json",
  ".png": "image/png",
  ".jpg": "image/jpeg",
  ".svg": "image/svg+xml",
  ".wasm": "application/wasm",
  ".ico": "image/x-icon",
  ".ttf": "font/ttf",
  ".otf": "font/otf",
  ".woff": "font/woff",
  ".woff2": "font/woff2",
};

const server = http.createServer((req, res) => {
  const url = new URL(req.url, `http://localhost:${port}`);
  let filePath = path.join(dir, url.pathname);

  // Try the exact file first
  if (fs.existsSync(filePath) && fs.statSync(filePath).isFile()) {
    const ext = path.extname(filePath);
    res.writeHead(200, { "Content-Type": mimeTypes[ext] || "application/octet-stream" });
    fs.createReadStream(filePath).pipe(res);
    return;
  }

  // SPA fallback: serve index.html for non-file paths
  const indexPath = path.join(dir, "index.html");
  if (fs.existsSync(indexPath)) {
    res.writeHead(200, { "Content-Type": "text/html" });
    fs.createReadStream(indexPath).pipe(res);
  } else {
    res.writeHead(404);
    res.end("Not found");
  }
});

server.listen(port, () => {
  console.log(`SPA server running at http://localhost:${port} (serving ${dir})`);
});
