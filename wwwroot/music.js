'use strict';

let catalogMode = 'vk';
let favorites = new Map();
let vkResults = null;
let catalogRequest = 0;
let discoveryQuery = '';
let pendingSearch = 0;

function showDiscovery(data) {
 if (data.itunes) {
  $('catalog-help').hidden = false;
  $('catalog-help').textContent = 'Результаты iTunes';
  showTracks(data.itunes, 'В iTunes ничего не найдено. Попробуйте другое название или имя исполнителя.');
  return;
 }
 const date = data.date.split('-').reverse().join('.');
 const query = $('search').value.trim();
 $('catalog-help').hidden = false;
 $('catalog-help').textContent = data.fullSearch && query ? 'Поиск по VK Музыке'
  : 'Россия: Топ-100 — ' + date;
 if (query && data.live.length) {
  showTracks(data.live.map(track => ({track})), '');
  return;
 }
 $('catalog-count').textContent = '';
 $('tracks').innerHTML = data.chart.length ? data.chart.map(track => '<article class="track">' +
  '<div class="cover blue"><span>' + track.rank + '</span></div><div class="track-info"><h3>' + escapeHtml(track.title) +
  '</h3><p>' + escapeHtml(track.artist) + '</p></div><div class="track-actions"><button class="action" data-chart-rank="' + track.rank +
  '" type="button" aria-label="Заказать ' + escapeHtml(track.title) + '">Заказать</button></div></article>').join('')
  : '<p class="empty">' + (data.fullSearch ? 'В VK совпадений нет.' : 'В Топ-100 совпадений нет. Поиск по всему VK сейчас недоступен.') + '</p>';
}

function trackMarkup(track, count) {
 const label = escapeHtml(track.title);
 if (track.itunesId) return '<article class="track">' + cover(track) + '<div class="track-info"><h3>' + label +
  '</h3><p>' + escapeHtml(track.artist) + '</p><div class="track-meta"><span>iTunes' +
  (track.explicit ? ' / explicit' : '') + '</span><span>' + duration(track.duration) + '</span></div></div>' +
  '<button class="action" data-itunes-track="' + track.itunesId + '" type="button" aria-label="Заказать ' + label + '">Заказать</button></article>';
 const favoriteId = favorites.get(track.vkId);
 const order = track.id ? 'data-track="' + track.id + '"' : 'data-vk-track="' + escapeHtml(track.vkId) + '"';
 return '<article class="track">' + cover(track) +
  '<div class="track-info"><h3>' + label + '</h3><p>' + escapeHtml(track.artist) + '</p>' +
  '<div class="track-meta"><span>VK Музыка' + (track.explicit ? ' / explicit' : '') + '</span><span>' + duration(track.duration) +
  '</span></div>' + (count === undefined ? '' : '<small>Исполнений: ' + count + '</small>') +
  '</div><div class="track-actions"><button class="add-track" ' + order + ' aria-label="Заказать ' + label + '">+</button>' +
  '<button class="favorite-track" data-vk-favorite="' + escapeHtml(track.vkId) + '" aria-pressed="' + Boolean(favoriteId) +
  '" aria-label="' + (favoriteId ? 'Убрать из избранного ' : 'В избранное ') + label + '">' +
  (favoriteId ? '★' : '☆') + '</button></div></article>';
}

function showTracks(tracks, emptyText) {
 $('catalog-count').textContent = tracks.length ? tracks.length + ' треков' : '';
 $('tracks').innerHTML = tracks.length ? tracks.map(({track,count}) => trackMarkup(track,count)).join('')
  : emptyText ? '<p class="empty">' + escapeHtml(emptyText) + '</p>' : '';
}

async function loadFavorites() {
 if (!session.table) { favorites = new Map(); return []; }
 const expected = session;
 const list = await api('/api/favorites');
 if (session === expected) favorites = new Map(list.map(track => [track.vkId, track.id]));
 return list;
}

async function loadCatalog() {
 if (catalogMode === 'vk' && pendingSearch && pendingSearch === catalogRequest) return;
 const generation = ++catalogRequest;
 if (catalogMode === 'custom') { showTracks([], ''); return; }
 if (catalogMode === 'vk') {
  const data = vkResults ?? await api('/api/catalog/vk/discover');
  if (generation !== catalogRequest) return;
  vkResults = data;
  showDiscovery(data);
  return;
 }
 const popular = catalogMode === 'popular';
 const rows = popular ? await api('/api/popular') : (await loadFavorites()).map(track => ({track}));
 if (generation !== catalogRequest) return;
 showTracks(rows, popular ? 'За последние 7 дней ещё не было завершённых выступлений с треками VK.'
  : session.table ? 'Нажмите на звёздочку рядом с треком VK, чтобы сохранить его на время визита.'
   : 'Выберите столик, чтобы открыть избранное.');
}

function bindCatalog() {
 $('catalog-modes').addEventListener('click', async event => {
  const button = event.target.closest('[data-mode]');
  if (!button || button.dataset.mode === catalogMode) return;
  catalogMode = button.dataset.mode;
  for (const item of $('catalog-modes').querySelectorAll('[data-mode]')) {
   item.classList.toggle('selected', item === button);
   item.setAttribute('aria-pressed', String(item === button));
  }
  $('search-form').hidden = catalogMode !== 'vk';
  $('custom-order-form').hidden = catalogMode !== 'custom';
  $('catalog-help').hidden = catalogMode !== 'popular';
  $('catalog-help').textContent = 'По числу завершённых выступлений Encore за 7 дней.';
  showTracks([], '');
  await loadCatalog().catch(error => toast(error.message,true));
 });
 $('search-form').addEventListener('submit', searchCatalog);
 $('custom-order-form').addEventListener('submit', submitCustomOrder);
 $('search').addEventListener('search', event => {
  if (!event.target.value) { discoveryQuery = ''; vkResults = null; loadCatalog().catch(error => toast(error.message,true)); }
 });
 $('tracks').addEventListener('click', useTrack);
 setInterval(refreshOpenChart, 60000);
 document.addEventListener('visibilitychange', refreshOpenChart);
}

async function submitCustomOrder(event) {
 event.preventDefault();
 if (!canOrder()) return;
 const form = event.currentTarget;
 const button = form.querySelector('[type=submit]');
 button.disabled = true;
 try {
  const title = $('custom-title').value.trim();
  const artist = $('custom-artist').value.trim();
  await api('/api/orders/custom', {method:'POST',body:JSON.stringify({title,artist})});
  form.reset();
  await refresh();
  toast('Заявка отправлена диджею.');
 } catch (error) { toast(error.message,true); }
 finally { button.disabled = false; }
}

async function refreshOpenChart() {
 if (document.hidden || view !== 'guest' || catalogMode !== 'vk' || discoveryQuery || $('search').value.trim()) return;
 const generation = catalogRequest;
 try {
  const data = await api('/api/catalog/vk/discover');
  if (generation !== catalogRequest || $('search').value.trim() || JSON.stringify(data) === JSON.stringify(vkResults)) return;
  vkResults = data;
  showDiscovery(data);
 } catch { /* Keep the last visible chart if the network request fails. */ }
}

async function searchCatalog(event) {
 event.preventDefault();
 const query = $('search').value.trim();
 if (query.length === 1) { toast('Введите минимум два символа.',true); return; }
 const generation = ++catalogRequest;
 discoveryQuery = query;
 pendingSearch = generation;
 showTracks([], 'Ищем композиции…');
 try {
  const results = query ? {itunes:(await api('/api/catalog/itunes?query=' + encodeURIComponent(query))).map(track => ({track}))}
   : await api('/api/catalog/vk/discover');
  if (generation !== catalogRequest) return;
  vkResults = results;
  showDiscovery(results);
 } catch (error) {
  if (generation !== catalogRequest) return;
  vkResults = null;
  showTracks([], error.message);
 } finally { if (pendingSearch === generation) pendingSearch = 0; }
}

function canOrder() {
 if (selectingTable) return false;
 if (session.table) return true;
 $('table-dialog').showModal();
 return false;
}

async function useTrack(event) {
 const itunes = event.target.closest('[data-itunes-track]');
 if (itunes) {
  if (!canOrder()) return;
  itunes.disabled = true;
  try {
   await api('/api/catalog/itunes/order', {method:'POST',body:JSON.stringify({id:Number(itunes.dataset.itunesTrack)})});
   await refresh();
   toast('Заявка отправлена диджею.');
  } catch (error) { toast(error.message,true); }
  finally { itunes.disabled = false; }
  return;
 }
 const chart = event.target.closest('[data-chart-rank]');
 if (chart) {
  const track = vkResults.chart.find(item => item.rank === Number(chart.dataset.chartRank));
  if (!track) return;
  if (canOrder()) await orderChart({...track, date:vkResults.date}, chart);
  return;
 }
 const button = event.target.closest('[data-track],[data-vk-track],[data-vk-favorite]');
 if (!button || !canOrder()) return;
 button.disabled = true;
 try {
  const favoriteKey = button.dataset.vkFavorite;
  if (favoriteKey) {
   const id = favorites.get(favoriteKey);
   if (id) await api('/api/favorites/' + id, {method:'DELETE'});
   else await api('/api/favorites/vk', {method:'POST', body:JSON.stringify({id:favoriteKey})});
   if (catalogMode !== 'favorites') await loadFavorites();
   await loadCatalog();
   toast(id ? 'Трек удалён из избранного.' : 'Трек сохранён в избранном на время визита.');
   return;
  }
  const isVkSearch = Boolean(button.dataset.vkTrack);
  await api(isVkSearch ? '/api/catalog/vk/order' : '/api/orders', {
   method:'POST', body:JSON.stringify(isVkSearch ? {id:button.dataset.vkTrack} : {trackId:Number(button.dataset.track)})
  });
  await refresh();
  toast('Ваша песня отправлена диджею.');
 } catch (error) { toast(error.message,true); }
 finally { button.disabled = false; }
}

async function orderChart(track, button) {
 button.disabled = true;
 try {
  await api('/api/orders/chart', {method:'POST', body:JSON.stringify({rank:track.rank,date:track.date})});
  await refresh();
  toast('Заявка отправлена диджею.');
  return true;
 } catch (error) { toast(error.message,true); return false; }
 finally { button.disabled = false; }
}
