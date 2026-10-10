"""Strict, bounded Godot execution with logs preserved even on failure."""
from pathlib import Path
import os
import shlex
import subprocess
import threading


def capture_stack(tool, process, log, cwd):
    if process.poll() is not None:
        return
    with log.with_suffix('.stack.log').open('w', encoding='utf-8') as output:
        try:
            subprocess.run([tool, 'report', '--process-id', str(process.pid)],
                           cwd=cwd, stdout=output, stderr=subprocess.STDOUT,
                           timeout=10, check=False)
        except (OSError, subprocess.TimeoutExpired) as error:
            output.write(f'Stack capture unavailable: {error}\n')


def run_godot(command, *, cwd, log, timeout=600, env=None):
    log = Path(log)
    log.parent.mkdir(parents=True, exist_ok=True)
    print(f"GODOT START [{log.name}]: {shlex.join(map(str, command))}", flush=True)
    try:
        with subprocess.Popen(command, cwd=cwd, env=env, text=True, encoding="utf-8",
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT) as process:
            tool = os.environ.get('OPEND2_STACK_TOOL')
            capture = threading.Timer(max(1, timeout - 15), capture_stack,
                                      args=(tool, process, log, cwd)) if tool else None
            if capture: capture.start()
            try:
                output, _ = process.communicate(timeout=timeout)
                result = subprocess.CompletedProcess(command, process.returncode, output)
            except subprocess.TimeoutExpired:
                process.kill()
                output, _ = process.communicate()
                raise subprocess.TimeoutExpired(command, timeout, output=output)
            finally:
                if capture:
                    capture.cancel()
                    capture.join()
    except subprocess.TimeoutExpired as error:
        output = error.stdout or ""
        if isinstance(output, bytes):
            output = output.decode("utf-8", errors="replace")
        log.write_text(output + f"\nTIMEOUT after {timeout}s\n", encoding="utf-8")
        raise SystemExit(f"Godot timed out; see {log}") from error
    log.write_text(result.stdout, encoding="utf-8")
    print(result.stdout, flush=True)
    result.check_returncode()
    # Keep ALL engine/script errors fatal, including a successful process exit.
    if "ERROR:" in result.stdout or "SCRIPT ERROR:" in result.stdout:
        raise SystemExit(f"Godot reported an error despite its exit code; see {log}")
    print(f"GODOT PASS [{log.name}]", flush=True)
    return result.stdout
