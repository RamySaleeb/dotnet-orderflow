import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { OutcomeAnnouncements, setTextIfChanged } from '../../src/OrderFlow/wwwroot/outcomes.mjs';
const order = (id = 'aaaaaaaa-0000-0000-0000-000000000001', status = 'Completed') => ({ id, status });

test('initial terminal history is silent, including later identical polls', () => {
  const tracker = new OutcomeAnnouncements();
  const history = [order(), order('bbbbbbbb-2', 'Failed')];
  assert.equal(tracker.observe(history), null);
  assert.equal(tracker.observe(history), null);
});
test('queued and retrying orders announce only their terminal transition', () => {
  const tracker = new OutcomeAnnouncements();
  for (const state of ['Queued', 'Processing', 'Retrying', 'Processing'])
    assert.equal(tracker.observe([order(undefined, state)]), null);
  assert.equal(tracker.observe([order()]), 'Order aaaaaaaa completed.');
  assert.equal(tracker.observe([order()]), null);
});
test('failure includes the short ID and understandable outcome', () => {
  const tracker = new OutcomeAnnouncements(); tracker.observe([]);
  assert.equal(tracker.observe([order(undefined, 'Failed')]), 'Order aaaaaaaa failed after exhausting retries.');
});
test('a fast order first seen terminal after hydration is announced', () => {
  const tracker = new OutcomeAnnouncements(); tracker.observe([]);
  assert.equal(tracker.observe([order()]), 'Order aaaaaaaa completed.');
});
test('multiple outcomes are batched into one announcement with every ID', () => {
  const tracker = new OutcomeAnnouncements(); tracker.observe([]);
  assert.equal(tracker.observe([order(), order('bbbbbbbb-2', 'Failed'), order('cccccccc-3')]),
    '3 orders finished. Completed: aaaaaaaa, cccccccc. Failed after exhausting retries: bbbbbbbb.');
});
test('unchanged polls, replay snapshots and reappearing orders are silent', () => {
  const tracker = new OutcomeAnnouncements(); tracker.observe([]);
  tracker.observe([order()]);
  assert.equal(tracker.observe([order()]), null);
  assert.equal(tracker.observe([]), null);
  assert.equal(tracker.observe([order(undefined, 'Processing')]), null);
  assert.equal(tracker.observe([order()]), null);
});
test('only new outcomes are included when old and new orders coexist', () => {
  const tracker = new OutcomeAnnouncements(); tracker.observe([order()]);
  assert.equal(tracker.observe([order(), order('bbbbbbbb-2', 'Failed')]),
    'Order bbbbbbbb failed after exhausting retries.');
});
test('an order submitted before hydration is announced without narrating history', () => {
  const tracker = new OutcomeAnnouncements(); tracker.submitted('bbbbbbbb-2');
  assert.equal(tracker.observe([order(), order('bbbbbbbb-2')]), 'Order bbbbbbbb completed.');
});
test('duplicate entries and full IDs with the same prefix are tracked separately', () => {
  const tracker = new OutcomeAnnouncements(); tracker.observe([]);
  assert.equal(tracker.observe([order(), order()]), 'Order aaaaaaaa completed.');
  assert.equal(tracker.observe([order('aaaaaaaa-0000-0000-0000-000000000002', 'Failed')]),
    'Order aaaaaaaa failed after exhausting retries.');
});
test('a fresh page session suppresses old outcomes again', () => {
  const before = new OutcomeAnnouncements(); before.observe([]); before.observe([order()]);
  assert.equal(new OutcomeAnnouncements().observe([order()]), null);
});
test('live-region writer does not mutate on unchanged polls or null outcomes', () => {
  let value = ''; let writes = 0;
  const node = { get textContent() { return value; }, set textContent(text) { value = text; writes++; } };
  setTextIfChanged(node, 'Connected');
  for (let i = 0; i < 50; i++) { setTextIfChanged(node, 'Connected'); setTextIfChanged(node, null); }
  assert.equal(writes, 1);
  setTextIfChanged(node, 'Connection interrupted');
  assert.equal(writes, 2);
});
test('dashboard has one stable polite atomic outcome region outside the timeline', async () => {
  const html = await readFile(new URL('../../src/OrderFlow/wwwroot/index.html', import.meta.url), 'utf8');
  assert.match(html, /<p[^>]*id="outcome-status"[^>]*role="status"[^>]*aria-live="polite"[^>]*aria-atomic="true"><\/p><div id="orders">/);
  assert.match(html, /<script src="\/app.js" type="module">/);
});
