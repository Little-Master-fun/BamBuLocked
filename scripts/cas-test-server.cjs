'use strict';
// Loopback-only, fixed-destination test proxy. Never logs request bodies or tickets.
const http = require('node:http');
const https = require('node:https');
const fs = require('node:fs');
const path = require('node:path');
const PORT = 18765;
const ORIGIN = `http://127.0.0.1:${PORT}`;
const page = path.join(__dirname, '..', 'cas-test.html');
const server = http.createServer((req, res) => {
  res.setHeader('Cache-Control', 'no-store');
  res.setHeader('X-Content-Type-Options', 'nosniff');
  const reject = (status, text) => { res.writeHead(status, {'Content-Type':'text/plain; charset=utf-8'}); res.end(text); };
  if (req.headers.host !== `127.0.0.1:${PORT}`) return reject(403, 'Invalid host');
  const url = new URL(req.url, ORIGIN);
  if (req.method === 'GET' && (url.pathname === '/' || url.pathname === '/cas-test.html')) {
    res.writeHead(200, {'Content-Type':'text/html; charset=utf-8'});
    return fs.createReadStream(page).pipe(res);
  }
  if (req.headers.origin && req.headers.origin !== ORIGIN) return reject(403, 'Cross-origin request rejected');
  if (req.headers['sec-fetch-site'] === 'cross-site') return reject(403, 'Cross-site request rejected');
  if (['POST','DELETE'].includes(req.method) && req.headers.origin !== ORIGIN) return reject(403, 'Same-origin request required');
  const endpoint = url.pathname.slice('/aut/'.length);
  const allowed = url.pathname.startsWith('/aut/') && (
    (req.method === 'GET' && ['login','restlet/tickets','v1/tickets','serviceValidate'].includes(endpoint)) ||
    (req.method === 'POST' && endpoint === 'restlet/tickets') ||
    (['POST','DELETE'].includes(req.method) && /^restlet\/tickets\/TGT-[A-Za-z0-9._~%-]+$/.test(endpoint))
  );
  if (!allowed) return reject(404, 'Unsupported test endpoint');
  let size = 0, chunks = [];
  req.on('data', chunk => {
    size += chunk.length;
    if (size > 16384) { chunks=[]; if (!res.writableEnded) reject(413, 'Request too large'); req.destroy(); }
    else chunks.push(chunk);
  });
  req.on('end', () => {
    if (res.writableEnded) return;
    const body = Buffer.concat(chunks); chunks=[];
    const upstream = https.request({
      hostname:'pass.sdu.edu.cn', port:443, path:'/cas/' + endpoint + url.search,
      method:req.method, rejectUnauthorized:true,
      headers:{'User-Agent':'axios/1.7.9 PrintGateTest/1.0', 'Accept':'application/json, text/plain, */*',
        ...(req.headers['content-type'] ? {'Content-Type':req.headers['content-type']} : {}),
        ...(['POST','DELETE'].includes(req.method) ? {'Content-Length':body.length} : {})}
    }, response => {
      const headers = {'Content-Type':response.headers['content-type'] || 'text/plain', 'X-CAS-Upstream':'response'};
      if (response.headers.location) headers.Location=response.headers.location;
      res.writeHead(response.statusCode, headers);
      let received=0;
      response.on('data', chunk => { received+=chunk.length; if(received>131072) {upstream.destroy();res.destroy();} });
      response.on('error', () => res.destroy());
      response.pipe(res);
    });
    upstream.setTimeout(12000, () => upstream.destroy(Object.assign(new Error('timeout'), {code:'ETIMEDOUT'})));
    upstream.on('error', error => {
      if (res.headersSent) return res.destroy();
      res.writeHead(502, {'Content-Type':'text/plain; charset=utf-8', 'X-CAS-Proxy-Error':'1'});
      const codes = new Set(['ETIMEDOUT','ECONNRESET','ENOTFOUND','ECONNREFUSED','CERT_HAS_EXPIRED','UNABLE_TO_VERIFY_LEAF_SIGNATURE','DEPTH_ZERO_SELF_SIGNED_CERT']);
      res.end('代理访问学校接口失败：' + (codes.has(error.code) ? error.code : 'TLS_OR_NETWORK_ERROR'));
    });
    res.on('close', () => { if(!res.writableFinished) upstream.destroy(); });
    upstream.end(body, () => body.fill(0));
  });
});
server.on('error', error => { console.error(error.code === 'EADDRINUSE' ? 'Port 18765 is already in use.' : 'Cannot start local test server.'); process.exitCode=1; });
server.listen(PORT, '127.0.0.1', () => console.log(`CAS test page: ${ORIGIN}/`));
