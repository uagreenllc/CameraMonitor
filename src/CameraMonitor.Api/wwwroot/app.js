async function api(path) {
  const r = await fetch(path);
  if (!r.ok) throw new Error(path + ' ' + r.status);
  return r.json();
}
function fmtTime(iso) { return iso ? new Date(iso).toLocaleString('uk-UA') : '—'; }
function statusLabel(status) { return { online: 'онлайн', offline: 'офлайн', starting: 'запуск' }[status] || status; }
function navHtml(active) {
  const l = [['index.html', 'Камери'], ['archive.html', 'Архів'], ['events.html', 'Події'],['status.html', 'Статус']];
  return l.map(([h, t]) => `<a href="${h}"${h === active ? ' class="active"' : ''}>${t}</a>`).join('');
}
function esc(s) { return String(s).replace(/[&<>"']/g, c => '&#' + c.charCodeAt(0) + ';'); }
Object.assign(window, { api, fmtTime, statusLabel, navHtml, esc });
