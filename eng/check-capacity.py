"""Validate 32 real WSS streams, room isolation and admission recovery in Realm/Open."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import uuid

ROOT = Path(__file__).resolve().parents[1]
NOT_RUN = ('physical_multi_pc', 'wan', 'packet_loss', 'manual_gui', 'sustained_combat_load',
           'maximum_server_capacity', 'long_duration', 'cpu_gc_tick_profiling',
           'server_backpressure_saturation')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--server-exe', type=Path, required=True)
    parser.add_argument('--seconds', type=int, default=30, help='Steady-state sample per mode, 1–3600 seconds; setup is additional')
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--output', type=Path, default=ROOT / 'artifacts/online/capacity')
    args = parser.parse_args()
    if not args.server_exe.is_file() or not 1 <= args.seconds <= 3600:
        parser.error('An existing server executable and duration 1–3600 are required')
    destination = args.output.resolve() / ('run-' + uuid.uuid4().hex[:12])
    destination.mkdir(parents=True)
    with args.server_exe.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    report = dict(result='FAIL', started_utc=datetime.now(timezone.utc).isoformat(),
                  commit=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
                  dirty=bool(subprocess.check_output(['git', 'status', '--porcelain'], cwd=ROOT, text=True).strip()),
                  server_executable_sha256=digest, requested_seconds_per_mode=args.seconds,
                  evidence=str(destination), modes={}, **dict.fromkeys(NOT_RUN, 'NOT_RUN'))
    try:
        with tempfile.TemporaryDirectory(prefix='opend2-capacity-private-') as private:
            env = os.environ.copy()
            env.setdefault('DOTNET_PROCESSOR_COUNT', '2')
            env['OPEND2_TEST_PFX_PASSWORD'] = secrets.token_hex(24)
            subprocess.run([args.dotnet, 'run', '--project', 'tests/OpenD2.OnlineTests', '-c', 'Release', '--',
                            '--capacity-server', str(args.server_exe.resolve()), '--fixtures', str(Path(private) / 'tls'),
                            '--output', str(destination / 'tls-capacity.json'), '--seconds', str(args.seconds)],
                           cwd=ROOT, env=env, check=True, timeout=540 + args.seconds * 2)
        for mode in ('realm', 'open'):
            evidence = json.loads((destination / ('capacity-' + mode + '.json')).read_text())
            if (evidence.get('result') != 'PASS' or evidence.get('mode') != mode
                    or len(evidence.get('checks', [])) != 6 or evidence.get('simultaneous_streams') != 32
                    or evidence.get('active_rooms') != 8 or evidence.get('rounds', 0) < 1
                    or evidence.get('room_interest') != 'v3'
                    or evidence.get('inputs') != evidence.get('rounds', 0) * 32
                    or evidence.get('matched_room_rounds') != evidence.get('rounds', 0) * 8
                    or evidence.get('sampled_seconds', 0) < args.seconds
                    or any(evidence.get(key) != 'NOT_RUN' for key in NOT_RUN)):
                raise ValueError('Incomplete capacity evidence: ' + mode)
            report['modes'][mode] = evidence
        report['result'] = 'PASS'
    finally:
        report['finished_utc'] = datetime.now(timezone.utc).isoformat()
        (destination / 'result.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
        print('STREAM CAPACITY ACCEPTANCE', report['result'], destination / 'result.json', flush=True)


if __name__ == '__main__':
    main()
