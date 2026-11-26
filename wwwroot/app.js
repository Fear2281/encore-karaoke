'use strict';
const $ = id => document.getElementById(id);
const escapeHtml = s => String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const duration = n => `${Math.floor(n/60)}:${String(n%60).padStart(2,'0')}`;
const statusText = s => ({waiting:'На модерации',approved:'Одобрено',playing:'На сцене',completed:'Завершено',rejected:'Отклонено',skipped:'Пропущено'}[s] || s);
let session={table:null,demo:false}, authenticated=false, view='guest', settingsLoaded=false, queueGeneration=0, selectingTable=false, queueUpdatedAt=0, toastTimer, catalogVersion='';
async function api(url, options={}) {
 const headers = {'Content-Type':'application/json', ...options.headers};
 const response=await fetch(url,{...options,headers});
 const data=await response.json().catch(()=>({}));
 if (!response.ok) {
  const message = data.error || ({429:'Слишком много запросов. Подождите минуту.',401:'Сеанс завершён. Войдите снова.'}[response.status]) || 'Не удалось выполнить запрос.';
  const error = new Error(message);
  error.status = response.status;
  throw error;
 }
 return data;
}
function toast(message,error=false){clearTimeout(toastTimer);$('toast').textContent=message;$('toast').classList.toggle('error',error);$('toast').hidden=false;toastTimer=setTimeout(()=>$('toast').hidden=true,5000);}
function cover(t){return `<div class="cover ${escapeHtml(t.color)}"><span>${escapeHtml(t.initials)}</span></div>`;}
function switchView(next) {
 view = next;
 sessionStorage.setItem('encore.view', next);
 $('guest-view').hidden = next !== 'guest';
 $('dj-view').hidden = next !== 'dj';
 $('guest-tab').classList.toggle('active', next === 'guest');
 $('dj-tab').classList.toggle('active', next === 'dj');
 if (next === 'dj') loadDj().catch(error => toast(error.message,true));
}
$('guest-tab').addEventListener('click',()=>switchView('guest'));
$('dj-tab').addEventListener('click',()=>switchView('dj'));
$('table-button').addEventListener('click',()=>$('table-dialog').showModal());
$('close-dialog').addEventListener('click',()=>$('table-dialog').close());
$('qr-button').addEventListener('click',()=>$('qr-dialog').showModal());
$('close-qr').addEventListener('click',()=>$('qr-dialog').close());
async function loadSession(){session=await api('/api/guest/session');$('table-button').textContent=session.table?`Столик № ${session.table} — сменить`:'Выбрать столик';$('demo-password').hidden=!session.demo;}
async function loadTables(){const tables=await api('/api/tables');$('table-choices').innerHTML=tables.map(t=>`<button class="table-choice" data-table="${t.number}" aria-label="Выбрать столик ${t.number}"><span>СТОЛИК</span><strong>${String(t.number).padStart(2,'0')}</strong></button>`).join('');}
$('table-choices').addEventListener('click', async event => {
 const button = event.target.closest('[data-table]');
 if (!button || selectingTable) return;
 selectingTable = button.disabled = true;
 try {
  await api('/api/guest/select', {method:'POST', body:JSON.stringify({tableNumber:Number(button.dataset.table)})});
  await loadSession();
  favorites = new Map();
  $('table-dialog').close();
  if (catalogMode === 'favorites') showTracks([], 'Загружаем избранное…');
  await refresh();
  await loadFavorites();
  await loadCatalog();
  toast('Столик ' + session.table + '. Выбирай трек!');
 } catch (error) { toast(error.message,true); }
 finally { selectingTable = button.disabled = false; }
});
function countdown(seconds, ticking) {
 return '<span data-wait="' + seconds + '" data-ticking="' + ticking + '">' + duration(seconds) + '</span>';
}
function nowMarkup(playing, liveCountdown = false) {
 const remaining = liveCountdown ? countdown(playing.remaining, playing.remaining) : duration(playing.remaining);
 return '<div class="now-disc"><span>e</span></div><div class="now-info"><h3>' + escapeHtml(playing.track.title) +
  '</h3><p>' + escapeHtml(playing.track.artist) + '</p><small>Столик ' + playing.table + ' — осталось ' + remaining +
  '</small><span class="equalizer"><i></i><i></i><i></i></span>' + '</div>';
}
function renderQueue(snapshot) {
 const {table,queue,history,playing,settings} = snapshot;
 queueUpdatedAt = performance.now();
 if (settings) {
  $('mood-label').textContent = settings.mood;
  $('accept-label').textContent = settings.accepting ? 'Принимаем ваши заявки' : 'Приём заявок на паузе';
  const version = JSON.stringify([settings.allowExplicit,settings.maxDuration]);
  if (catalogVersion && catalogVersion !== version && catalogMode !== 'vk') loadCatalog().catch(error => toast(error.message,true));
  catalogVersion = version;
 }
 $('queue-table').textContent = table ? 'СТОЛИК № ' + table : 'МОЙ СТОЛИК';
 $('end-visit').hidden = !table;
 $('on-air').hidden = !playing;
 $('now-playing').hidden = !playing;
 $('now-playing').innerHTML = playing ? nowMarkup(playing, true) : '';
 $('queue-count').textContent = queue.length;
 $('guest-queue').innerHTML = queue.length ? queue.map(order => {
  const wait = order.waitSeconds ? ' — до выхода ≈ ' + countdown(order.waitSeconds, order.tickingSeconds)
   : ' — ' + (order.status === 'approved' ? 'следующая песня' : 'после одобрения');
  return '<div class="queue-item' + (order.ownTable ? ' own-table' : '') + '"><div class="queue-item-info"><h4>' +
   order.position + ' — ' + escapeHtml(order.track.title) + '</h4><p>' + escapeHtml(order.track.artist) +
   '</p><small>Столик ' + order.table + ' — ' + statusText(order.status) + wait + '</small></div></div>';
 }).join('') : '<p class="empty">В очереди пока нет песен</p>';
 $('guest-history').innerHTML = table ? historyMarkup(history) : '<p>Подключите столик, чтобы увидеть свои заявки.</p>';
}
function historyMarkup(items) {
 return items.length ? items.map(order => '<div class="history-row">' + escapeHtml(order.track.title) + ' — столик ' +
  order.table + '<br>' + statusText(order.status) + (order.reason ? ' — ' + escapeHtml(order.reason) : '') + '</div>').join('')
  : '<p class="empty">Завершённых заявок пока нет.</p>';
}
async function refresh() {
 const generation = ++queueGeneration;
 const snapshot = await api('/api/queue');
 if (generation !== queueGeneration) return;
 if (session.table !== snapshot.table || session.expires !== snapshot.expires) {
  if (selectingTable) return;
  await loadSession();
  if (session.table !== snapshot.table || generation !== queueGeneration) return;
  renderQueue(snapshot);
  {
   await loadFavorites();
   await loadCatalog();
  }
 } else {
  renderQueue(snapshot);
 }
 if (view === 'dj' && authenticated) await refreshDj();
}
setInterval(() => {
 const elapsed = Math.floor((performance.now() - queueUpdatedAt) / 1000);
 for (const element of document.querySelectorAll('[data-wait]')) {
  element.textContent = duration(Math.max(0, Number(element.dataset.wait) - Math.min(elapsed, Number(element.dataset.ticking))));
 }
}, 1000);
$('end-visit').addEventListener('click', async () => {
 if (selectingTable || !window.confirm('Завершить визит? Ожидающие заявки будут сняты, история и избранное очистятся.')) return;
 selectingTable = true;
 try {
  await api('/api/guest/end', {method:'POST'});
  await loadSession();
  favorites = new Map();
  renderQueue({table:null,queue:[],history:[],playing:null});
  await refresh();
  await loadCatalog();
  toast('Визит завершён.');
 } catch (error) { toast(error.message,true); }
 finally { selectingTable = false; }
});

$('login-form').addEventListener('submit', async event => {
 event.preventDefault();
 try {
  await api('/api/dj/login', {method:'POST', body:JSON.stringify({password:$('password').value})});
  $('password').value = '';
  await loadDj();
  toast('Пульт открыт.');
 } catch (error) { toast(error.message,true); }
});

$('logout').addEventListener('click', async () => {
 try {
  await api('/api/dj/logout', {method:'POST'});
  authenticated = settingsLoaded = false;
  await loadDj();
 } catch (error) { toast(error.message,true); }
});

let djTables = new Map();

async function loadDj() {
 authenticated = (await api('/api/dj/session')).authenticated;
 $('login-panel').hidden = authenticated;
 $('dj-dashboard').hidden = !authenticated;
 $('logout').hidden = !authenticated;
 if (!authenticated) return;
 await refreshDj();
 const tables = await api('/api/dj/tables');
 djTables = new Map(tables.map(table => [table.number, table]));
 $('tables').innerHTML = tables.map(table => '<div class="table-staff-row"><strong>Столик ' + table.number +
  '</strong><button class="action" type="button" data-qr-table="' + table.number + '">QR</button>' +
  '<button class="action" type="button" data-close-table="' + table.number + '">Закрыть визиты</button></div>').join('');
}

async function refreshDj() {
 const state = await api('/api/dj/state');
 const playing = state.playing;
 $('stats').innerHTML = [[state.stats.total,'Всего заявок'],[state.stats.waiting,'В очереди'],[state.stats.completed,'Завершено']]
  .map(([value,label]) => '<div class="stat"><strong>' + value + '</strong><span>' + label + '</span></div>').join('');
 $('dj-playing').innerHTML = playing ? '<div class="now-playing">' + nowMarkup(playing) + '</div><div class="dj-actions">' +
  djButton(playing, 'complete', 'Завершить') + djButton(playing, 'skip', 'Пропустить') + '</div>'
  : '<p class="empty">Одобри первую заявку и нажми «На сцену», чтобы запустить таймер</p>';
 $('dj-queue').innerHTML = state.queue.length ? state.queue.map(order =>
  '<article class="dj-card">' + cover(order.track) + '<div class="dj-card-info"><h3>' + order.position + ' — ' + escapeHtml(order.track.title) + '</h3><p>' +
  escapeHtml(order.track.artist) + ' — ' + duration(order.track.duration) + '</p><small>Столик ' + order.table + ' — ' +
  statusText(order.status) +
  '</small></div><div class="dj-actions">' +
  (order.status === 'waiting' ? djButton(order, 'approve', 'Одобрить')
   : djButton(order, 'play', 'На сцену', Boolean(playing) || order.position !== 1)) +
  djButton(order, 'reject', 'Отклонить') + djButton(order, 'skip', 'Пропустить') + '</div></article>').join('')
  : '<p class="empty">Новые заявки появятся здесь.</p>';
 $('dj-history').innerHTML = historyMarkup(state.history);
 if (!settingsLoaded) {
  $('mood').value = state.settings.mood;
  $('limit').value = state.settings.tableLimit;
  $('duration').value = state.settings.maxDuration;
  $('accepting').checked = state.settings.accepting;
  $('explicit').checked = state.settings.allowExplicit;
  settingsLoaded = true;
 }
}

function djButton(order, action, label, disabled = false) {
 const kind = ['approve','play','complete'].includes(action) ? ' good' : action === 'reject' ? ' bad' : '';
 return '<button class="action' + kind + '" data-order="' + order.id + '" data-action="' + action + '"' +
  (disabled ? ' disabled' : '') + '>' + label + '</button>';
}

$('dj-dashboard').addEventListener('click', async event => {
 const button = event.target.closest('[data-action]');
 if (!button) return;
 const {order,action} = button.dataset;
 button.disabled = true;
 try {
  await api('/api/dj/orders/' + order + '/' + action, {method:'POST'});
  await refresh();
  if (catalogMode === 'popular') await loadCatalog();
 } catch (error) { toast(error.message,true); }
 finally { button.disabled = false; }
});

$('tables').addEventListener('click', async event => {
 const qr = event.target.closest('[data-qr-table]');
 const close = event.target.closest('[data-close-table]');
 if (qr) {
  const table = djTables.get(Number(qr.dataset.qrTable));
  const image = '/api/dj/tables/' + table.number + '/qr';
  $('table-qr-title').textContent = 'Столик № ' + table.number;
  $('table-qr-image').src = image;
  $('table-qr-image').alt = 'QR столика № ' + table.number;
  $('download-table-qr').href = image;
  $('download-table-qr').download = 'encore-table-' + table.number + '.svg';
  const address = new URL(table.url);
  $('table-qr-address').textContent = ['localhost','127.0.0.1'].includes(address.hostname)
   ? 'Локальный адрес. Для телефона нужен QR с доменом сервера или адресом в общей сети.' : address.origin;
  $('table-qr-dialog').showModal();
 }
 if (!close || !window.confirm('Закрыть все визиты столика № ' + close.dataset.closeTable + '? Активные заявки будут сняты.')) return;
 close.disabled = true;
 try {
  const result = await api('/api/dj/tables/' + close.dataset.closeTable + '/close', {method:'POST'});
  await refresh();
  toast(result.closed ? 'Визиты завершены. Столик готов для новых гостей.' : 'У столика нет открытых визитов.');
 } catch (error) { toast(error.message,true); }
 finally { close.disabled = false; }
});
$('close-table-qr').addEventListener('click', () => $('table-qr-dialog').close());
$('print-table-qr').addEventListener('click', () => window.print());
$('settings-form').addEventListener('submit',async e=>{e.preventDefault();try{await api('/api/dj/settings',{method:'PUT',body:JSON.stringify({mood:$('mood').value,tableLimit:Number($('limit').value),maxDuration:Number($('duration').value),accepting:$('accepting').checked,allowExplicit:$('explicit').checked})});await refresh();toast('Настройки вечера сохранены.');}catch(err){toast(err.message,true);}});
async function start(){try{await loadSession();await loadTables();await loadFavorites();await loadCatalog();await refresh();}catch(e){toast('Сервер недоступен. '+e.message,true);}}
let refreshing=false;
setInterval(async()=>{if(document.hidden||refreshing)return;refreshing=true;try{await refresh();}catch(e){if(view==='dj'&&authenticated)loadDj().catch(()=>{});}finally{refreshing=false;}},3000);
bindCatalog();
if (sessionStorage.getItem('encore.view') === 'dj') switchView('dj');
start();
