"""Run both modes over TLS and validate evidence from two real Godot processes."""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import platform
import secrets
import subprocess
import sys
import tempfile
import uuid

ROOT = Path(__file__).resolve().parents[1]
COMMON = {'login', 'character_selected', 'same_room', 'combat_sync', 'item_sync',
          'host_disconnect', 'host_reconnect', 'host_resumed_input',
          'guest_disconnect', 'guest_reconnect', 'guest_resumed_input', 'server_restart'}
INVITE = {'invite_applied', 'ca_missing_rejected', 'ca_mismatch_rejected',
          'malformed_invite_preserves_settings', 'invite_edits_rejected', 'server_checked'}


def validate_evidence(evidence, mode, tls):
    """Fail closed on missing stages, mismatched rooms or unsupported physical claims."""
    results = []
    for role in ('host', 'guest'):
        result = json.loads((Path(evidence) / (role + '-result.json')).read_text(encoding='utf-8'))
        required = COMMON | (INVITE if tls else set())
        if tls:
            required |= {'invite_generated', 'room_invite_generated'} if role == 'host' else {'invited_room_joined'}
        if result.get('result') != 'PASS' or result.get('role') != role or result.get('mode') != mode:
            raise ValueError('Client identity, mode or result mismatch: ' + role)
        if not required <= set(result.get('acceptance', [])):
            raise ValueError('Missing acceptance stages for ' + role + ': ' + ', '.join(sorted(required - set(result.get('acceptance', [])))))
        if result.get('observations') != 'room-websocket-stream' or result.get('http_state_polling') is not False:
            raise ValueError('Gameplay did not use the production room stream: ' + role)
        if any(result.get(key) != 'NOT_RUN' for key in ('manual_gui', 'multi_pc', 'wan')):
            raise ValueError('Loopback evidence cannot claim physical acceptance: ' + role)
        for key in ('room', 'character'):
            if uuid.UUID(result[key]).int == 0:
                raise ValueError('Missing ' + key + ' identity: ' + role)
        results.append(result)
    if results[0]['room'] != results[1]['room'] or results[0]['character'] == results[1]['character']:
        raise ValueError('Clients must share one room with distinct owned characters')
    return {r['role']: r['acceptance'] for r in results}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--server-exe', required=True, type=Path, help='Published self-contained server from this revision')
    client = parser.add_mutually_exclusive_group(required=True)
    client.add_argument('--godot', help='Pinned editor executable, or auto; build client Debug first')
    client.add_argument('--client-packages', type=Path, help='Release package directory')
    parser.add_argument('--preset', choices=['Linux', 'Windows', 'macOS'])
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--output', type=Path, default=ROOT / 'artifacts/online/two-client-acceptance')
    parser.add_argument('--windowed', action='store_true', help='Render automated TLS clients; manual GUI remains NOT_RUN')
    args = parser.parse_args()
    if not __debug__:
        parser.error('Run without Python -O; integration assertions must be enabled')
    if args.client_packages and not args.preset:
        parser.error('--client-packages requires --preset')
    if not args.server_exe.is_file():
        parser.error('--server-exe must exist')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    run = output / ('run-' + uuid.uuid4().hex[:12])
    run.mkdir()
    revision = subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True, capture_output=True, check=True).stdout.strip()
    dirty = bool(subprocess.run(['git', 'status', '--porcelain'], cwd=ROOT, text=True, capture_output=True, check=True).stdout.strip())
    report = dict(result='FAIL', scope='one-machine-two-godot-processes', commit=revision, dirty=dirty,
                  platform=platform.platform(), started_utc=datetime.now(timezone.utc).isoformat(),
                  evidence=str(run), single_client_per_pc='NOT_RUN', tls_contracts='NOT_RUN',
                  modes={}, physical_multi_pc='NOT_RUN', wan='NOT_RUN', manual_gui='NOT_RUN',
                  clipboard_gestures='NOT_RUN', nat_mapping='NOT_IMPLEMENTED', relay='NOT_IMPLEMENTED')
    env = os.environ.copy()
    env.setdefault('DOTNET_PROCESSOR_COUNT', '2')
    command = [sys.executable, str(ROOT / 'eng/check-online.py'), '--dotnet', args.dotnet,
               '--server-exe', str(args.server_exe.resolve())]
    if args.godot:
        command += ['--godot', args.godot]
    else:
        command += ['--client-packages', str(args.client_packages.resolve()), '--preset', args.preset]
    def execute(command, timeout=300):
        subprocess.run(command, cwd=ROOT, env=env, check=True, timeout=timeout)
    try:
        # Existing guard exercises actual normal clients, profile changes, remote-test
        # rejection, normal exit and crash recovery. Never open a remote smoke bypass.
        execute(command + ['--output', str(run / 'baseline')])
        baseline = json.loads((run / 'baseline/result.json').read_text())
        if baseline.get('single_client_per_pc') != 'PASS':
            raise RuntimeError('Single-client policy was not verified')
        report['single_client_per_pc'] = 'PASS'
        with tempfile.TemporaryDirectory(prefix='opend2-acceptance-private-') as temporary:
            fixtures = Path(temporary) / 'tls'
            env['OPEND2_TEST_PFX_PASSWORD'] = secrets.token_hex(24)
            execute([args.dotnet, 'run', '--project', 'tests/OpenD2.OnlineTests', '-c', 'Release', '--',
                     '--tls-server', str(args.server_exe.resolve()), '--fixtures', str(fixtures),
                     '--output', str(run / 'tls-contracts.json')])
            report['tls_contracts'] = 'PASS'
            env['OPEND2_TEST_HOST_EXE'] = str(args.server_exe.resolve())
            env['OPEND2_TEST_HOST_TLS_FIXTURES'] = str(fixtures)
            for mode in ('realm', 'open'):
                destination = run / mode
                execute(command + ['--mode', mode, '--tls-fixtures', str(fixtures), '--output', str(destination)]
                        + (['--windowed'] if args.windowed else []))
                result = json.loads((destination / 'result.json').read_text())
                if result.get('result') != 'PASS' or result.get('invite_to_gameplay') != 'PASS':
                    raise RuntimeError('Incomplete TLS gameplay: ' + mode)
                report['modes'][mode] = result
        report['result'] = 'PASS'
    finally:
        report['finished_utc'] = datetime.now(timezone.utc).isoformat()
        (output / 'result.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
        print('TWO CLIENT ACCEPTANCE', report['result'], output / 'result.json', flush=True)


if __name__ == '__main__':
    main()
