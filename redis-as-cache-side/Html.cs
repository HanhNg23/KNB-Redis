namespace CacheAsideDemo;

public static class Html
{
    public const string Page = """
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<title>Redis cache-aside demo</title>
<style>
  body { font-family: system-ui, sans-serif; max-width: 860px; margin: 2rem auto; padding: 0 1rem; }
  section { border: 1px solid #ccc; border-radius: 6px; padding: 1rem; margin-bottom: 1rem; }
  button { margin-right: .4rem; }
  pre { background: #f4f4f4; padding: .6rem; overflow: auto; min-height: 3rem; }
  .hit { color: #0a7d2c; font-weight: bold; }
  .miss { color: #b3261e; font-weight: bold; }
</style>
</head>
<body>
<h1>Redis cache-aside demo</h1>
<p>Primary store latency: 150 ms. Cache TTL: 30 s.</p>

<section>
  <h3>Read through the cache</h3>
  Product: <select id="id"></select>
  <button onclick="readIt()">Read</button>
  <button onclick="invalidate()">Invalidate cache</button>
  <div id="readStatus"></div>
  <pre id="readOut"></pre>
</section>

<section>
  <h3>Update primary (invalidates cache)</h3>
  Field: <select id="field"><option>price_cents</option><option>stock</option><option>name</option></select>
  Value: <input id="value" value="699">
  <button onclick="update()">Update</button>
  <pre id="updateOut"></pre>
</section>

<section>
  <h3>Stampede test</h3>
  Readers: <input id="readers" type="number" value="50" min="1" max="200">
  <button onclick="stampede()">Run</button>
  <pre id="stampedeOut"></pre>
</section>

<section>
  <h3>Stats</h3>
  <button onclick="stats()">Refresh</button>
  <button onclick="resetStats()">Reset</button>
  <pre id="statsOut"></pre>
</section>

<script>
const $ = id => document.getElementById(id);
const show = (el, o) => $(el).textContent = JSON.stringify(o, null, 2);
async function call(url, method = 'GET') {
  const r = await fetch(url, { method });
  return r.json();
}
async function init() {
  const ids = await call('/api/products');
  $('id').innerHTML = ids.map(i => `<option>${i}</option>`).join('');
  stats();
}
async function readIt() {
  const o = await call(`/api/read/${$('id').value}`);
  $('readStatus').innerHTML = o.hit === undefined ? '' :
    `<span class="${o.hit ? 'hit' : 'miss'}">${o.hit ? 'CACHE HIT' : 'CACHE MISS'}</span>` +
    ` - total ${o.total_latency_ms} ms, TTL ${o.ttl_seconds === null ? '-' : o.ttl_seconds.toFixed(1)} s`;
  show('readOut', o);
  stats();
}
async function invalidate() {
  show('readOut', await call(`/api/invalidate/${$('id').value}`, 'POST'));
}
async function update() {
  const q = new URLSearchParams({ field: $('field').value, value: $('value').value });
  show('updateOut', await call(`/api/update/${$('id').value}?${q}`, 'POST'));
}
async function stampede() {
  $('stampedeOut').textContent = 'running...';
  show('stampedeOut', await call(`/api/stampede/${$('id').value}?readers=${$('readers').value}`, 'POST'));
  stats();
}
async function stats() { show('statsOut', await call('/api/stats')); }
async function resetStats() { await call('/api/reset', 'POST'); stats(); }
init();
</script>
</body>
</html>
""";
}
