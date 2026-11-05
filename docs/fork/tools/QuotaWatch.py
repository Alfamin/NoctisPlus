"""Read-only quota checkpoint via Codex's documented app-server protocol.
No model calls, purchases, resets, credentials or session data are read/written.
At 90% this writes WRAP-UP-NOW; agents must check it before every work block.
It is a warning mechanism, not a platform-enforced interruption of a running turn.
"""
import argparse
import datetime
import json
import os
from pathlib import Path
import queue
import subprocess
import threading
import time


def snapshot():
    candidates = list((Path(os.environ['LOCALAPPDATA']) / 'OpenAI/Codex/bin').glob('*/codex.exe'))
    exe = max(candidates, key=lambda p: p.stat().st_mtime)
    p = subprocess.Popen([str(exe), 'app-server', '--stdio'], stdin=subprocess.PIPE,
                         stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True,
                         creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    messages = queue.Queue()
    def read():
        for line in p.stdout:
            try: messages.put(json.loads(line))
            except ValueError: pass
    threading.Thread(target=read, daemon=True).start()
    def ask(number, method, params):
        p.stdin.write(json.dumps({'id': number, 'method': method, 'params': params}) + '\n')
        p.stdin.flush()
        end = time.monotonic() + 25
        while time.monotonic() < end:
            answer = messages.get(timeout=max(.1, end - time.monotonic()))
            if answer.get('id') == number:
                if 'error' in answer: raise RuntimeError('Quota service rejected the read')
                return answer['result']
        raise TimeoutError('Quota read timed out')
    try:
        ask(1, 'initialize', {'clientInfo': {'name': 'noctis_checkpoint_watch', 'version': '1.0'}})
        p.stdin.write('{"method":"initialized","params":{}}\n'); p.stdin.flush()
        result = ask(2, 'account/rateLimits/read', {})
        bucket = (result.get('rateLimitsByLimitId') or {}).get('codex') or result.get('rateLimits') or {}
        values = {k: bucket.get(k) for k in ('primary', 'secondary')}
        values['wrapUp'] = any((values[k] or {}).get('usedPercent', 0) >= 90 for k in ('primary', 'secondary'))
        values['checkedUtc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        return values
    finally:
        p.terminate()
        try: p.wait(timeout=5)
        except subprocess.TimeoutExpired: p.kill(); p.wait()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--once', action='store_true')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    while True:
        try:
            state = snapshot()
            temp = args.output / 'quota-status.tmp'
            temp.write_text(json.dumps(state, indent=2), encoding='utf-8')
            temp.replace(args.output / 'quota-status.json')
            if state['wrapUp']:
                (args.output / 'WRAP-UP-NOW').write_text(state['checkedUtc'], encoding='utf-8')
                print('WRAP-UP NOW: usage is at least 90%. Save work; start no new tasks.', flush=True)
                break
            (args.output / 'WRAP-UP-NOW').unlink(missing_ok=True)
            if args.once: break
        except Exception as ex:
            # Do not replace a valid reading with zeros or print service/auth payloads.
            print('Quota check unavailable: ' + type(ex).__name__, flush=True)
            if args.once: raise SystemExit(1)
        time.sleep(60)
