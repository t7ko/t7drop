// A stand-in for GitHub's latest-release API, for trying self-updates without publishing.
// Usage: node windows/scripts/fake-release-server.js <folder> <port>
// The folder holds latest.json (GitHub's release JSON, with browser_download_url pointing at
// http://127.0.0.1:<port>/T7Drop.exe and .../T7Drop.exe.sig), T7Drop.exe and T7Drop.exe.sig
// (windows/obj/Tests.exe sign <release key> <folder>/T7Drop.exe; the updater takes only what
// the release key signed). Start T7Drop.exe with T7DROP_RELEASES_URL=http://127.0.0.1:<port>/latest
// (and T7DROP_HOME for a test copy); AGENTS.md has the whole recipe.
const http = require('http');
const fs = require('fs');
const path = require('path');
const [folder, port] = process.argv.slice(2);
http.createServer((request, response) => {
  console.log(`${new Date().toISOString()} ${request.method} ${request.url} ${request.headers['user-agent']}`);
  const file = { '/latest': 'latest.json', '/T7Drop.exe': 'T7Drop.exe', '/T7Drop.exe.sig': 'T7Drop.exe.sig' }[request.url];
  if (!file) {
    response.writeHead(404);
    response.end();
    return;
  }
  response.writeHead(200, { 'Content-Type': file.endsWith('.json') ? 'application/json' : 'application/octet-stream' });
  response.end(fs.readFileSync(path.join(folder, file)));
}).listen(Number(port), '127.0.0.1');
