"""Publish, archive, extract and run the self-contained server with two packaged clients."""
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import secrets
import subprocess
import sys
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
preset = {'Linux': 'Linux', 'Darwin': 'macOS', 'Windows': 'Windows'}[platform.system()]
osname = {'Linux': 'linux', 'macOS': 'osx', 'Windows': 'win'}[preset]
arch = 'arm64' if platform.machine().lower() in ('arm64', 'aarch64') else 'x64'
rid = osname + '-' + arch
os.environ.setdefault('DOTNET_PROCESSOR_COUNT', '2')
output = ROOT / 'artifacts/server-packages'
output.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix='opend2-server-package-') as temporary:
    stage = Path(temporary)/'stage'
    subprocess.run(['dotnet', 'publish', 'src/OpenD2.Server', '-c', 'Release', '-r', rid,
                    '--self-contained', 'true', '-o', str(stage), '-m:1'], cwd=ROOT, check=True)
    shutil.copyfile(ROOT/'docs/migration/ONLINE_PLAY.md', stage/'ONLINE_PLAY.md')
    shutil.copyfile(ROOT/'docs/migration/ONLINE_NETWORK.md', stage/'ONLINE_NETWORK.md')
    package = output/f'OpenD2-server-{rid}.zip'
    with zipfile.ZipFile(package, 'w', zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(stage.rglob('*')):
            if path.is_file(): archive.write(path, path.relative_to(stage))
    extracted = Path(temporary)/'extracted'
    with zipfile.ZipFile(package) as archive:
        archive.extractall(extracted)
        if preset != 'Windows':
            for entry in archive.infolist():
                if not entry.is_dir(): (extracted/entry.filename).chmod((entry.external_attr >> 16) & 0o777)
    executable = extracted/('OpenD2.Server.exe' if preset == 'Windows' else 'OpenD2.Server')
    env = {k: v for k, v in os.environ.items() if not k.upper().startswith('DOTNET_ROOT') and k.upper() != 'DOTNET_HOST_PATH'}
    env['PATH'] = os.pathsep.join([str(Path(os.environ['SystemRoot'])/'System32'), os.environ['SystemRoot']]) if preset == 'Windows' else '/usr/bin:/bin:/usr/sbin:/sbin'
    check = [sys.executable, 'eng/check-online.py', '--server-exe', str(executable),
             '--client-packages', 'artifacts/packages', '--preset', preset]
    subprocess.run(check, cwd=ROOT, env=env, check=True)
    rendered = os.environ.get('OPEND2_ONLINE_RENDERED') == '1'
    if rendered:
        subprocess.run(check + ['--windowed', '--output', 'artifacts/online/rendered'], cwd=ROOT, env=env, check=True)
    # Temporary roots are scoped to the explicit client connection, never installed
    # in the OS trust store. Neither PFX/private keys nor account DBs are artifacts.
    certificates = Path(temporary) / 'tls'
    tls_env = os.environ.copy()
    tls_env['OPEND2_TEST_PFX_PASSWORD'] = secrets.token_hex(24)
    subprocess.run(['dotnet', 'run', '--project', 'tests/OpenD2.OnlineTests', '-c', 'Release', '--no-build', '--',
        '--tls-server', str(executable), '--fixtures', str(certificates), '--output', 'artifacts/online/tls-contracts.json'],
        cwd=ROOT, env=tls_env, check=True)
    env['OPEND2_TEST_PFX_PASSWORD'] = tls_env['OPEND2_TEST_PFX_PASSWORD']
    subprocess.run(check + ['--tls-fixtures', str(certificates), '--output', 'artifacts/online/tls'], cwd=ROOT, env=env, check=True)
    with package.open('rb') as stream: digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    (output/f'server-{rid}.json').write_text(json.dumps({'commit': os.environ.get('GITHUB_SHA', 'local'), 'runtime': rid,
        'file': package.name, 'sha256': digest, 'bytes': package.stat().st_size,
        'extracted_server_and_two_clients': 'PASS', 'two_client_gameplay': 'PASS', 'tls_certificates': 'PASS', 'tls_two_client_gameplay': 'PASS',
        'room_stream_contracts': 'PASS', 'room_stream_transport': 'WSS',
        'rendered_gameplay': 'PASS' if rendered else 'NOT_RUN', 'gui': 'NOT_RUN'}, indent=2)+'\n')
print('ONLINE SERVER PACKAGE PASS', rid)
