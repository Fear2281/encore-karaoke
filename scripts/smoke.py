"""Exercise the Encore HTTP flows with a temporary data directory."""

from datetime import datetime, timedelta, timezone
from http.cookiejar import CookieJar
import json
import os
from pathlib import Path
import shutil
import subprocess
from tempfile import TemporaryDirectory
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
BASE = 'http://127.0.0.1:5081'
DOTNET = os.environ.get('DOTNET') or shutil.which('dotnet') or 'dotnet'
DLL = ROOT / 'bin/Debug/net8.0/Encore.dll'
CHECKS = 0


class Client:
    def __init__(self):
        self.cookies = CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.cookies))

    def request(self, route, method='GET', body=None, expected=200, headers=None):
        global CHECKS
        if body is not None and not isinstance(body, bytes):
            body = json.dumps(body).encode()
        request = urllib.request.Request(BASE + route, data=body, method=method, headers={
            'Content-Type': 'application/json', **(headers or {}),
        })
        try:
            response = self.opener.open(request, timeout=10)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            raw = response.read()
            assert response.code == expected, (method, route, response.code, expected, raw[:240])
            CHECKS += 1
            if 'application/json' in response.headers.get('Content-Type', ''):
                return json.loads(raw)
            return raw

with TemporaryDirectory(prefix='encore-check-') as directory:
    root = Path(directory)
    env = {**os.environ, 'ASPNETCORE_ENVIRONMENT': 'Development', 'ENCORE_DATA_DIR': directory,
           'ENCORE_DJ_PASSWORD': 'test-password', 'ENCORE_VK_TOKEN': '',
           'ENCORE_PUBLIC_URL': 'https://karaoke.example.test', 'Logging__LogLevel__Default': 'Warning'}
    process = None

    def start():
        global process
        process = subprocess.Popen([DOTNET, str(DLL), '--urls', BASE], cwd=ROOT, env=env,
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for _ in range(100):
            if process.poll() is not None:
                raise RuntimeError('Encore exited during startup')
            try:
                with urllib.request.urlopen(BASE + '/api/health', timeout=1):
                    return
            except (urllib.error.URLError, TimeoutError):
                time.sleep(.1)
        raise RuntimeError('Encore did not become ready')

    def stop():
        if process and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()

    try:
        start()
        guest, neighbor, dj = Client(), Client(), Client()
        assert guest.request('/api/health')['modules'] == 5
        guest.request('/api/catalog', expected=404)
        guest.request('/api/catalog/vk?query=test', expected=503)
        guest.request('/api/catalog/vk/order', 'POST', {'id': '-123_456'}, 401)
        guest.request('/api/orders/upload', 'POST', expected=404)
        guest.request('/api/dj/tables', expected=401)
        dj.request('/api/dj/login', 'POST', {'password': 'wrong'}, 401)
        dj.request('/api/dj/login', 'POST', {'password': 'test-password'})
        tables = dj.request('/api/dj/tables')
        assert len(tables) == 12 and len({t['url'] for t in tables}) == 12
        for table in tables:
            assert table['url'] == 'https://karaoke.example.test/table/' + table['token']
            assert b'<svg' in dj.request(f"/api/dj/tables/{table['number']}/qr")
        guest.request('/table/bad-token', expected=404)
        first_table = '/table/' + tables[0]['token']
        guest.request(first_table)
        neighbor.request('/table/' + tables[1]['token'])
        assert guest.request('/api/guest/session')['table'] == 1
        chart = guest.request('/api/catalog/vk/discover')
        def order(client, rank):
            return client.request('/api/orders/chart', 'POST', {'date':chart['date'], 'rank':rank})['id']
        first = order(guest, 1)
        third = order(guest, 2)
        second = order(neighbor, 3)
        assert [o['id'] for o in guest.request('/api/queue')['queue']] == [first, second, third]
        assert [o['ownTable'] for o in neighbor.request('/api/queue')['queue']] == [False, True, False]
        assert len(dj.request('/api/dj/state')['queue']) == 3
        assert guest.request('/api/favorites') == []
        media = '/api/dj/orders/' + first
        guest.request('/api/dj/state', expected=401)
        dj.request('/api/dj/orders/' + second + '/approve', 'POST')
        dj.request('/api/dj/orders/' + second + '/play', 'POST', expected=409)
        dj.request(media + '/approve', 'POST')
        dj.request(media + '/play', 'POST')
        assert neighbor.request('/api/queue')['playing']['id'] == first
        dj.request(media + '/complete', 'POST')
        assert guest.request('/api/queue')['history'][0]['id'] == first
        assert neighbor.request('/api/queue')['history'] == []
        guest.request('/api/guest/end', 'POST', expected=403, headers={'Origin': 'https://other.example'})
        guest.request('/api/guest/end', 'POST')
        assert guest.request('/api/guest/session')['table'] is None
        guest.request(first_table)
        assert guest.request('/api/queue')['history'] == []
        assert dj.request('/api/dj/tables/2/close', 'POST')['closed'] == 1
        assert neighbor.request('/api/queue')['table'] is None
        assert guest.request('/api/popular') == []
        guest.request(first_table)
        active = order(guest, 4)
        stop()
        start()
        assert guest.request('/api/queue')['queue'][0]['id'] == active
        assert dj.request('/api/dj/tables')[0]['url'] == tables[0]['url']
        stop()
        saved_path = root / 'state.json'
        saved = json.loads(saved_path.read_text(encoding='utf-8'))
        expired = (datetime.now(timezone.utc) - timedelta(minutes=1)).isoformat()
        for visit in saved['Visits'].values():
            visit['Expires'] = expired
        saved_path.write_text(json.dumps(saved), encoding='utf-8')
        start()
        assert guest.request('/api/queue')['table'] is None
        for _ in range(30):
            if not dj.request('/api/dj/state')['queue']:
                break
            time.sleep(.15)
        assert any(row['id'] == active and row['status'] == 'skipped' for row in dj.request('/api/dj/state')['history'])
        assert chart['chart'][0]['title'].encode() in dj.request('/api/dj/export')
        print(f'PASS: {CHECKS} HTTP checks; QR, shared table queue, visit privacy, expiry and persistence.')
    finally:
        stop()
