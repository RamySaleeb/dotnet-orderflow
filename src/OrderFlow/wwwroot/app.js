import { OutcomeAnnouncements, setTextIfChanged } from './outcomes.mjs';
const outcomes = new OutcomeAnnouncements();
const $ = id => document.getElementById(id);
// A random per-tab capability separates visitors. It is not user authentication.
let session;
try { session = sessionStorage.getItem('orderflow-session'); if (!session) { session = crypto.randomUUID(); sessionStorage.setItem('orderflow-session', session); } }
catch { session = crypto.randomUUID(); }
let last = null;
let busy = false;
let previous = '';
let refreshing = false;
const emptyMarkup = $('orders').innerHTML;
const names = { Success: 'Smooth delivery', Recover: 'A bump in the road', Exhaust: 'Retry budget exhausted' };
function message(text, error = false) { setTextIfChanged($('message'), text); $('message').classList.toggle('error', error); }
async function request(path, options = {}) {
  const response = await fetch(path, { ...options, headers: { 'X-Demo-Session': session, ...options.headers }, signal: AbortSignal.timeout(10000) });
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(data.error || (response.status === 429 ? 'The demo is busy. Please try again in a minute.' : `API request failed (${response.status}).`));
  return data;
}
async function submit(replay) {
  if (busy) return;
  if (!replay) last = { key: crypto.randomUUID(), scenario: new FormData($('order-form')).get('scenario') };
  if (!last) return;
  busy = true; $('submit').setAttribute('aria-disabled', 'true'); $('replay').setAttribute('aria-disabled', 'true');
  try {
    const data = await request('/api/orders', { method: 'POST', headers: { 'Content-Type': 'application/json', 'Idempotency-Key': last.key }, body: JSON.stringify({ scenario: last.scenario }) });
    if (!data.replayed) outcomes.submitted(data.order.id);
    message(data.replayed ? 'Same key, same order. No duplicate job was queued.' : 'Accepted (HTTP 202). The background worker takes it from here.');
    await refresh();
  } catch (error) { message(`${error.message} You can repeat this request safely.`, true); }
  finally { busy = false; $('submit').removeAttribute('aria-disabled'); $('replay').removeAttribute('aria-disabled'); $('replay').disabled = false; }
}
$('order-form').addEventListener('submit', event => { event.preventDefault(); submit(false); });
$('replay').addEventListener('click', () => submit(true));
function element(tag, text, className) { const node = document.createElement(tag); if (text) node.textContent = text; if (className) node.className = className; return node; }
async function refresh() {
  if (refreshing) return;
  refreshing = true;
  try {
    const orders = await request('/api/orders');
    setTextIfChanged($('connection'), 'Connected · updates every second · temporary, per-tab orders');
    setTextIfChanged($('outcome-status'), outcomes.observe(orders));
    const json = JSON.stringify(orders);
    if (json === previous) return;
    previous = json; $('count').textContent = `${orders.length} order${orders.length === 1 ? '' : 's'}`;
    if (!orders.length) { $('orders').innerHTML = emptyMarkup; return; }
    const nodes = orders.map(order => {
      const article = element('article', '', 'order'); const head = element('div', '', 'order-head');
      const title = element('div'); title.append(element('h3', names[order.scenario] || 'Order'), element('small', `FIELD KIT / ${order.id.slice(0, 8)} / attempt ${order.attempts} of 3`));
      head.append(title, element('span', order.status, `badge ${order.status}`)); article.append(head);
      const events = element('ol', '', 'events'); events.setAttribute('aria-label', 'Order events');
      for (const event of order.events) { const li = element('li', event.message); const time = element('time', new Date(event.at).toLocaleTimeString()); time.dateTime = event.at; li.append(time); events.append(li); }
      article.append(events); return article;
    });
    $('orders').replaceChildren(...nodes);
  } catch { setTextIfChanged($('connection'), 'Connection interrupted. Retrying automatically; existing orders remain visible.'); }
  finally { refreshing = false; }
}
async function poll() { await refresh(); setTimeout(poll, 1000); }
poll();
