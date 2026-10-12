// Page-session state. Initial history is silent; subsequent terminal outcomes are
// announced once per full UUID, even if an order disappears and reappears.
export class OutcomeAnnouncements {
  #initialized = false;
  #announced = new Set();
  #submitted = new Set();

  submitted(id) { this.#submitted.add(id); }

  observe(orders) {
    const completed = [];
    const failed = [];
    for (const order of orders) {
      if (order.status !== 'Completed' && order.status !== 'Failed') continue;
      if (this.#announced.has(order.id)) continue;
      this.#announced.add(order.id);
      // Preserve an order submitted before the first successful GET completes.
      if (!this.#initialized && !this.#submitted.has(order.id)) continue;
      (order.status === 'Completed' ? completed : failed).push(order.id.slice(0, 8));
      this.#submitted.delete(order.id);
    }
    this.#initialized = true;
    const count = completed.length + failed.length;
    if (!count) return null;
    if (count === 1) return completed.length
      ? `Order ${completed[0]} completed.`
      : `Order ${failed[0]} failed after exhausting retries.`;
    // One atomic update per snapshot, rather than competing announcements.
    return [`${count} orders finished.`,
      completed.length ? `Completed: ${completed.join(', ')}.` : '',
      failed.length ? `Failed after exhausting retries: ${failed.join(', ')}.` : '']
      .filter(Boolean).join(' ');
  }
}

// Rewriting identical live-region text can still trigger assistive technology.
export function setTextIfChanged(node, text) {
  if (text !== null && node.textContent !== text) node.textContent = text;
}
