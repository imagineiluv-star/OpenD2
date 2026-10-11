"""Run real TLS contracts and bounded Realm/Open byte-relay disruption probes."""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import uuid

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--server-exe', type=Path, required=True)
    parser.add_argument('--seconds', type=int, default=30, help='Impaired steady-state duration per mode, 1–3600 seconds')
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--output', type=Path, default=ROOT / 'artifacts/online/network')
    args = parser.parse_args()
    if not args.server_exe.is_file() or not 1 <= args.seconds <= 3600:
        parser.error('An existing server executable and duration 1–3600 are required')
    destination = args.output.resolve() / ('run-' + uuid.uuid4().hex[:12])
    destination.mkdir(parents=True)
    report = dict(result='FAIL', started_utc=datetime.now(timezone.utc).isoformat(),
                  commit=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
                  dirty=bool(subprocess.check_output(['git', 'status', '--porcelain'], cwd=ROOT, text=True).strip()),
                  requested_seconds_per_mode=args.seconds, evidence=str(destination), modes={},
                  physical_multi_pc='NOT_RUN', wan='NOT_RUN', packet_loss='NOT_RUN',
                  manual_gui='NOT_RUN', capacity='NOT_RUN', long_duration='NOT_RUN')
    try:
        with tempfile.TemporaryDirectory(prefix='opend2-network-private-') as private:
            env = os.environ.copy()
            env.setdefault('DOTNET_PROCESSOR_COUNT', '2')
            env['OPEND2_TEST_PFX_PASSWORD'] = secrets.token_hex(24)
            subprocess.run([args.dotnet, 'run', '--project', 'tests/OpenD2.OnlineTests', '-c', 'Release', '--',
                            '--tls-server', str(args.server_exe.resolve()), '--fixtures', str(Path(private) / 'tls'),
                            '--output', str(destination / 'tls-contracts.json'), '--network-seconds', str(args.seconds)],
                           cwd=ROOT, env=env, check=True, timeout=300 + args.seconds * 2)
        for mode in ('realm', 'open'):
            evidence = json.loads((destination / ('network-' + mode + '.json')).read_text())
            if (evidence.get('result') != 'PASS' or evidence.get('mode') != mode
                    or len(evidence.get('checks', [])) != 6 or evidence.get('matched_states', 0) < 1
                    or evidence.get('sampled_seconds', 0) < args.seconds
                    or any(evidence.get(key) != 'NOT_RUN' for key in ('physical_multi_pc', 'wan', 'packet_loss', 'manual_gui', 'capacity', 'long_duration'))):
                raise ValueError('Incomplete network evidence: ' + mode)
            report['modes'][mode] = evidence
        report['result'] = 'PASS'
    finally:
        report['finished_utc'] = datetime.now(timezone.utc).isoformat()
        (destination / 'result.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
        print('NETWORK ACCEPTANCE', report['result'], destination / 'result.json', flush=True)


if __name__ == '__main__':
    main()
