(() => {
  const card = document.querySelector('[data-project-opens="optimization"]');
  const counter = document.querySelector('[data-opens-count]');
  if (!card || !counter) return;
  const endpoint = '/api/project-opens';
  const display = data => {
    if (Number.isSafeInteger(data.opens) && data.opens >= 161) {
      counter.textContent = new Intl.NumberFormat('ru-RU').format(data.opens);
    }
  };
  const refresh = () => fetch(endpoint, {cache:'no-store', signal:AbortSignal.timeout(8000)})
    .then(r => r.ok ? r.json() : Promise.reject()).then(display).catch(() => {});
  function countOpen(event) {
    if (event.type === 'auxclick' && event.button !== 1) return;
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    const id = Array.from(bytes, b => b.toString(16).padStart(2,'0')).join('');
    // Navigation remains native; keepalive lets this small request finish after leaving.
    fetch(endpoint, {method:'POST', headers:{'Content-Type':'application/json'},
      body:JSON.stringify({event:id}), keepalive:true, cache:'no-store'})
      .then(r => r.ok ? r.json() : Promise.reject()).then(display).catch(() => {});
  }
  card.addEventListener('click', countOpen);
  card.addEventListener('auxclick', countOpen);
  window.addEventListener('pageshow', refresh);
  document.addEventListener('visibilitychange', () => { if (!document.hidden) refresh(); });
  refresh();
})();
