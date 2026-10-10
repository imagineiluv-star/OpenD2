"""Exercise the actual client startup lease, including process-exit recovery."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time


def check_single_instance(command, output, cwd):
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    children, handles = [], []
    checks = []
    with tempfile.TemporaryDirectory(prefix='opend2-instance-') as temporary:
        def launch(name, frames=600, arguments=None, validation=False):
            env = os.environ.copy()
            env.pop('OPEND2_ONLINE_VALIDATION_RUN', None)
            if validation:
                env['OPEND2_ONLINE_VALIDATION_RUN'] = 'instance-check'
            # A different Linux profile must NOT grant another normal client lease.
            for key in ('XDG_DATA_HOME', 'XDG_CACHE_HOME'):
                folder = Path(temporary) / name / key
                folder.mkdir(parents=True)
                env[key] = str(folder)
            handle = (output / (name + '.log')).open('w', encoding='utf-8')
            handles.append(handle)
            child = subprocess.Popen(command + ['--quit-after', str(frames), '--max-fps', '60', '--']
                                     + (arguments or ['--smoke-test']), cwd=cwd, env=env,
                                     stdout=handle, stderr=subprocess.STDOUT)
            children.append(child)
            return child

        def text(name):
            value = (output / (name + '.log')).read_text(encoding='utf-8')
            assert 'ERROR:' not in value and 'SCRIPT ERROR:' not in value, name
            return value

        def ready(child, name):
            deadline = time.monotonic() + 60
            while time.monotonic() < deadline:
                value = text(name)
                assert child.poll() is None, (name, child.returncode)
                if 'OPEND2_M0_READY' in value:
                    assert 'OPEND2_INSTANCE_ACQUIRED' in value, name
                    return
                time.sleep(0.1)
            raise TimeoutError(name + ' startup timed out')

        def denied(name, code, marker, **kwargs):
            child = launch(name, **kwargs)
            assert child.wait(timeout=60) == code, (name, child.returncode)
            value = text(name)
            assert marker in value and 'OPEND2_M0_READY' not in value, name
            assert 'OPEND2_ONLINE_UI_READY' not in value, name
            checks.append(name)

        try:
            first = launch('first', frames=1800)
            ready(first, 'first')
            denied('duplicate', 73, 'OPEND2_INSTANCE_BLOCKED')
            denied('normal-with-test-env', 73, 'OPEND2_INSTANCE_BLOCKED', validation=True)
            denied('unguarded-smoke', 74, 'OPEND2_VALIDATION_REFUSED',
                   arguments=['--online-smoke', '--run=instance-check', '--server=http://127.0.0.1:1'])
            denied('remote-smoke', 74, 'OPEND2_VALIDATION_REFUSED', validation=True,
                   arguments=['--online-smoke', '--run=instance-check', '--server=https://example.invalid'])
            assert first.poll() is None, 'Original client must remain active through rejection tests'
            assert first.wait(timeout=90) == 0
            text('first')
            second = launch('after-normal-exit', frames=3600)
            ready(second, 'after-normal-exit')
            checks.append('normal-exit-releases-lease')
            second.kill()
            second.wait(timeout=15)
            text('after-normal-exit')
            third = launch('after-crash')
            ready(third, 'after-crash')
            assert third.wait(timeout=60) == 0
            text('after-crash')
            checks.append('crash-releases-lease')
            (output / 'result.json').write_text(json.dumps({'result': 'PASS', 'checks': checks,
                'mode': 'actual-client-headless', 'manual_gui': 'NOT_RUN',
                'cross_os_user_sessions': 'NOT_RUN'}, indent=2) + '\n')
            print('SINGLE CLIENT PER PC PASS:', ', '.join(checks), flush=True)
        finally:
            for child in children:
                if child.poll() is None:
                    child.kill()
                    child.wait(timeout=15)
            for handle in handles:
                handle.close()
