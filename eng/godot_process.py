"""Strict, bounded Godot execution with logs preserved even on failure."""
from pathlib import Path
import shlex
import subprocess


def run_godot(command, *, cwd, log, timeout=600, env=None):
    log = Path(log)
    log.parent.mkdir(parents=True, exist_ok=True)
    print(f"GODOT START [{log.name}]: {shlex.join(map(str, command))}", flush=True)
    try:
        result = subprocess.run(command, cwd=cwd, env=env, text=True, encoding="utf-8",
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=timeout)
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
