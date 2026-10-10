"""Real HTTP clients, server restart, and optional two-process Godot transport validation."""
import argparse
import json
import os
from pathlib import Path
import secrets
import hashlib
import plistlib
import platform
import tarfile
import zipfile
import socket
import ssl
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--output', type=Path, default=ROOT / 'artifacts/online')
    parser.add_argument('--godot')
    parser.add_argument('--tls-fixtures', type=Path, help='Private ephemeral certificates produced by TLS contracts')
    parser.add_argument('--windowed', action='store_true', help='Render two automated client windows; not manual GUI acceptance')
    parser.add_argument('--client-packages', type=Path)
    parser.add_argument('--preset', choices=['Linux', 'Windows', 'macOS'])
    parser.add_argument('--mode', choices=['realm', 'open'], default='realm')
    parser.add_argument('--server-exe', type=Path)
    parser.add_argument('--server', type=Path, help='Published server DLL to validate instead of build output')
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    report = {'single_client_per_pc': 'NOT_RUN', 'http': 'NOT_RUN', 'restart': 'NOT_RUN', 'godot_transport': 'NOT_RUN', 'godot_gameplay': 'NOT_RUN', 'rendered': 'NOT_RUN', 'gui': 'NOT_RUN'}
    with tempfile.TemporaryDirectory(prefix='opend2-online-') as temporary:
        private = Path(temporary)
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0))
            port = listener.getsockname()[1]
        url = f'{"https" if args.tls_fixtures else "http"}://127.0.0.1:{port}'
        server_env = os.environ.copy()
        context = None
        tls_args = []
        if args.tls_fixtures:
            certificates = args.tls_fixtures.resolve()
            server_env['Kestrel__Certificates__Default__Path'] = str(certificates / 'valid.pfx')
            server_env['Kestrel__Certificates__Default__Password'] = os.environ['OPEND2_TEST_PFX_PASSWORD']
            context = ssl.create_default_context(cafile=str(certificates / 'ca.pem'))
            tls_args = ['--ca=' + str(certificates / 'ca.pem')]
        report['transport'] = 'HTTPS' if context else 'HTTP_LOOPBACK'
        server = args.server or ROOT / 'src/OpenD2.Server/bin/Release/net10.0/OpenD2.Server.dll'
        command = ([str(args.server_exe.resolve())] if args.server_exe else [args.dotnet, str(server)]) + ['--urls', url, '--data', str(private/'data'), '--mode', args.mode]
        client_command = None
        if args.godot:
            godot = (ROOT/'.local-tools/godot-path.txt').read_text().strip() if args.godot == 'auto' else args.godot
            client_command = [godot, '--path', 'src/OpenD2.Client'] + (['--rendering-method', 'gl_compatibility', '--audio-driver', 'Dummy', '--max-fps', '60'] if args.windowed else ['--headless'])
        if args.client_packages:
            metadata = json.loads((args.client_packages/f'package-{args.preset}.json').read_text())
            package = args.client_packages / metadata['file']
            with package.open('rb') as source:
                assert hashlib.file_digest(source, 'sha256').hexdigest() == metadata['sha256']
            extracted = private/'client'; extracted.mkdir()
            if args.preset == 'Linux':
                with tarfile.open(package) as archive: archive.extractall(extracted, filter='data')
                executable = extracted/'OpenD2.x86_64'
            elif args.preset == 'Windows':
                with zipfile.ZipFile(package) as archive: archive.extractall(extracted)
                executable = extracted/'OpenD2.exe'
            else:
                subprocess.run(['ditto', '-x', '-k', str(package), str(extracted)], check=True)
                app = next(extracted.glob('*.app'))
                info = plistlib.loads((app/'Contents/Info.plist').read_bytes())
                executable = app/'Contents/MacOS'/info['CFBundleExecutable']
            client_command = [str(executable)] + (['--rendering-method', 'gl_compatibility', '--audio-driver', 'Dummy', '--max-fps', '60'] if args.windowed else ['--headless'])
        process = None
        children = []
        handles = []

        def request(method, path, body=None, token=None, expected=200):
            headers = {'Content-Type': 'application/json'}
            if token:
                headers['Authorization'] = 'Bearer ' + token
            data = json.dumps(body).encode() if body is not None else (b'' if method == 'POST' else None)
            try:
                response = urllib.request.urlopen(urllib.request.Request(url + path, data, headers, method=method), timeout=10, context=context)
            except urllib.error.HTTPError as error:
                response = error
            with response:
                content = response.read()
                assert response.status == expected, (method, path, response.status, expected, content[:200])
                return json.loads(content) if content else None

        def start(index):
            nonlocal process
            handle = (private/f'server-{index}.log').open('w', encoding='utf-8'); handles.append(handle)
            process = subprocess.Popen(command, cwd=ROOT, stdout=handle, stderr=subprocess.STDOUT, env=server_env)
            for _ in range(200):
                if process.poll() is not None:
                    raise RuntimeError('Server exited during startup')
                try:
                    assert request('GET', '/health')['mode'] == args.mode
                    return
                except (OSError, AssertionError):
                    time.sleep(0.1)
            raise TimeoutError('Server startup timed out')

        try:
            if client_command and not args.windowed and not args.tls_fixtures:
                from client_instance_check import check_single_instance
                check_single_instance(client_command, output / 'single-instance', ROOT)
                report['single_client_per_pc'] = 'PASS'
            start(1)
            passwords = [secrets.token_hex(16) for _ in range(3)]
            users = ['alice', 'bob', 'eve']
            tokens = [request('POST', '/v1/register', {'username': u, 'password': p})['token'] for u, p in zip(users, passwords)]
            a, b, eve = tokens
            request('GET', '/v1/characters', expected=401)
            request('POST', '/v1/login', {'username': 'alice', 'password': secrets.token_hex(16)}, expected=401)
            ca = request('POST', '/v1/characters', {'name': 'Warrior'}, a)['id']
            cb = request('POST', '/v1/characters', {'name': 'Ranger'}, b)['id']
            request('DELETE', '/v1/characters/' + ca, token=b, expected=404)
            room = request('POST', '/v1/rooms', {'character': ca, 'name': 'Camp', 'password': passwords[2]}, a)
            path = '/v1/rooms/' + room['id']
            request('POST', path+'/join', {'character': cb, 'password': 'wrong-password-value'}, b, 403)
            request('POST', path+'/join', {'character': cb, 'password': passwords[2]}, b)
            request('GET', path, token=eve, expected=403)
            request('POST', path+'/start', token=b, expected=403)
            request('POST', path+'/start', token=a)
            request('POST', path+'/input', {'sequence': 1, 'kind': 0, 'x': 1, 'actor': 2}, a, 400)
            request('POST', path+'/input', {'sequence': 1, 'kind': 0, 'x': -1}, a)
            request('POST', path+'/input', {'sequence': 1, 'kind': 0, 'x': -1}, a, 409)
            time.sleep(0.2)
            request('POST', path+'/input', {'sequence': 2, 'kind': 0}, a)
            time.sleep(0.1)
            peer = request('GET', path, token=b)
            assert next(e for e in peer['entities'] if e['id']['value'] == 1)['position']['x'] < 768
            assert next(e for e in peer['entities'] if e['id']['value'] == 2)['position'] == {'x': 768, 'y': 1280}
            request('POST', path+'/save', token=a)
            report['http'] = 'PASS'
            # Abrupt process loss: compare against the last committed server file, not a later live tick.
            process.kill(); process.wait(timeout=10)
            db = json.loads((private/'data/realm.json').read_text())
            persisted = next(r for r in db['Rooms'] if r['Id'] == room['id'])['Game']
            raw = (private/'data/realm.json').read_text()
            assert all(secret not in raw for secret in passwords + tokens)
            start(2)
            request('GET', path, token=a, expected=401)
            a = request('POST', '/v1/login', {'username': 'alice', 'password': passwords[0]})['token']
            restored = request('POST', path+'/join', {'character': ca, 'password': ''}, a)
            assert restored['tick'] == persisted['Tick']
            assert restored['nextSequence'] == 3
            for e in restored['entities']:
                saved = next(s for s in persisted['Entities'] if s['Id']['Value'] == e['id']['value'])
                assert e['position'] == {'x': saved['Position']['X'], 'y': saved['Position']['Y']}
                assert e['health'] == saved['Health']
            report['restart'] = 'PASS'
            request('POST', path+'/close', token=a)
            request('POST', path+'/close', token=a, expected=404)
            if client_command:
                run = secrets.token_hex(4)
                evidence = output / ('gameplay-' + run)
                evidence.mkdir()
                for role in ('host', 'guest'):
                    handle = (evidence/f'godot-{role}.log').open('w', encoding='utf-8'); handles.append(handle)
                    client_env = os.environ.copy()
                    client_env['OPEND2_ONLINE_VALIDATION_RUN'] = run
                    if platform.system() == 'Linux':
                        # Independent clients represent independent user profiles. Avoid
                        # concurrent engine shader-cache creation in the same user://.
                        for variable, suffix in [('XDG_DATA_HOME', 'data'), ('XDG_CACHE_HOME', 'cache')]:
                            folder = private / (role + '-' + suffix)
                            folder.mkdir()
                            client_env[variable] = str(folder)
                    children.append((role, subprocess.Popen(client_command + ['--', '--online-smoke', '--mode='+args.mode, '--server='+url, '--role='+role, '--run='+run, '--evidence='+str(evidence.resolve())] + tls_args, cwd=ROOT,
                        stdout=handle, stderr=subprocess.STDOUT, env=client_env)))
                deadline = time.monotonic() + 150
                restarted = False
                while any(child.poll() is None for _, child in children):
                    if time.monotonic() >= deadline:
                        raise TimeoutError('Two-client gameplay timed out')
                    for role, child in children:
                        if child.poll() not in (None, 0):
                            raise RuntimeError(f'{role} gameplay failed; see {evidence}/godot-{role}.log')
                    if not restarted and all((evidence / (role + '-restart-ready')).exists() for role in ('host', 'guest')):
                        process.kill(); process.wait(timeout=10)
                        start(3)
                        (evidence / 'server-restarted').write_text('ready')
                        restarted = True
                    time.sleep(0.1)
                assert restarted, 'Both Godot clients must survive a hard server restart'
                for role, child in children:
                    assert child.returncode == 0, role
                    text = (evidence/f'godot-{role}.log').read_text(encoding='utf-8')
                    assert 'ERROR:' not in text and 'SCRIPT ERROR:' not in text, role
                    assert 'OPEND2_ONLINE_TRANSPORT_PASS '+role in text, role
                    assert 'OPEND2_ONLINE_STREAM_PASS '+role in text, role
                    assert 'OPEND2_ONLINE_GAMEPLAY_PASS '+role in text, role
                    if args.mode == 'open':
                        assert 'OPEND2_OPEN_PROFILE_UI_PASS DIALOG_GESTURES_NOT_RUN' in text, role
                    result = json.loads((evidence / (role + '-result.json')).read_text())
                    assert result['result'] == 'PASS' and len(result['checks']) >= 9, role
                    assert result['observations'] == 'room-websocket-stream' and result['http_state_polling'] is False, role
                    if args.windowed:
                        assert result['rendering'] == 'PASS' and len(list(evidence.glob(role + '-*.png'))) == 5, role
                if platform.system() == 'Linux':
                    profiles = [json.loads((evidence / (role + '-result.json')).read_text())['user_data'] for role in ('host', 'guest')]
                    assert profiles[0] != profiles[1], 'Godot clients must have independent user data directories'
                report['godot_transport'] = 'PASS'
                report['room_stream'] = 'WSS' if args.tls_fixtures else 'WS_LOOPBACK'
                report['godot_gameplay'] = 'PASS'
                report['open_profile_ui_binding'] = 'PASS' if args.mode == 'open' else 'NOT_RUN'
                report['rendered'] = 'PASS' if args.windowed else 'NOT_RUN'
            print('ONLINE HTTP / RESTART / GODOT:', json.dumps(report))
        finally:
            for _, child in children:
                if child.poll() is None: child.kill(); child.wait(timeout=10)
            if process and process.poll() is None: process.terminate(); process.wait(timeout=10)
            for handle in handles: handle.close()
            report['mode'] = args.mode
            (output/'result.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
            # No account DB, passwords or bearer tokens are uploaded.


if __name__ == '__main__':
    main()
