import json
import hashlib
from decimal import Decimal
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
import uuid

from verify import check, VerificationFailure
from recipe_scenario import execute as execute_recipe

ROOT = Path(__file__).resolve().parents[2]
DLL = ROOT / 'src' / 'Assistant' / 'bin' / 'Release' / 'net10.0' / 'Assistant.dll'
SQL = '''SELECT COALESCE(json_agg(row_to_json(t)), '[]'::json)::text FROM (
SELECT "SubjectId" AS subject_id, "BotId" AS bot_id, "ChatId" AS chat_id,
"MessageId" AS message_id, "TopicId" AS topic_id, "Value" AS value, "Unit" AS unit
FROM observations ORDER BY "Id") t;'''


def run(args, timeout=20):
    return subprocess.run(args, check=True, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                          text=True, encoding='utf-8', timeout=timeout).stdout.strip()


def receipt_value(value):
    if isinstance(value, Decimal):
        return str(value)
    raise TypeError('unsupported receipt value')


def witness(prompt, timeout=120):
    print(prompt, flush=True)
    result = queue.Queue(maxsize=1)
    def read():
        try:
            result.put((True, sys.stdin.readline()))
        except Exception:
            result.put((False, ''))
    threading.Thread(target=read, daemon=True).start()
    try:
        ok, line = result.get(timeout=timeout)
    except queue.Empty as error:
        raise VerificationFailure('operator witness timeout') from error
    if not ok or not line:
        raise VerificationFailure('operator witness unavailable')
    try:
        value = json.loads(line)
    except json.JSONDecodeError as error:
        raise VerificationFailure('malformed operator witness') from error
    if not isinstance(value, dict):
        raise VerificationFailure('malformed operator witness')
    return value


class Child:
    def __init__(self, environment):
        self.events = queue.Queue()
        self.count = 0
        self.requests = 0
        self.recipe_requests = 0
        self.stopped = False
        self.process = subprocess.Popen(['dotnet', str(DLL)], cwd=ROOT, env=environment,
                                        stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                        text=True, encoding='utf-8', errors='replace')
        def read():
            assert self.process.stdout is not None
            for line in self.process.stdout:
                marker = line.strip()
                if marker in ('polling_ready', 'extraction_requested', 'extraction_completed', 'recipe_requested'):
                    self.events.put(marker)
        self.reader = threading.Thread(target=read, daemon=True)
        self.reader.start()

    def wait_for(self, marker, seconds):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                raise VerificationFailure('application exited')
            try:
                current = self.events.get(timeout=min(0.2, max(0.01, deadline - time.monotonic())))
            except queue.Empty:
                continue
            if current == 'extraction_completed':
                self.count += 1
            elif current == 'extraction_requested':
                self.requests += 1
            elif current == 'recipe_requested':
                self.recipe_requests += 1
            if current == marker:
                return
        raise VerificationFailure('application marker timeout')

    def drain(self):
        while True:
            try:
                marker = self.events.get_nowait()
            except queue.Empty:
                return
            if marker == 'extraction_completed':
                self.count += 1
            elif marker == 'extraction_requested':
                self.requests += 1
            elif marker == 'recipe_requested':
                self.recipe_requests += 1

    def stop(self):
        if self.stopped:
            return
        if self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)
        self.reader.join(timeout=5)
        if self.reader.is_alive():
            raise VerificationFailure('application output reader did not stop')
        self.drain()
        if self.process.stdout is not None:
            self.process.stdout.close()
        self.stopped = True


def rows(container):
    result = run(['docker', 'exec', container, 'psql', '-U', 'postgres', '-d', 'runner',
                  '-t', '-A', '-c', SQL])
    return json.loads(result, parse_float=Decimal)


def private_directory(path):
    path = Path(path).expanduser().resolve()
    existing = path
    while not existing.exists():
        existing = existing.parent
    probe = subprocess.run(['git', '-C', str(existing), 'rev-parse', '--is-inside-work-tree'],
                           stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, timeout=10)
    if probe.returncode == 0 and probe.stdout.strip() == 'true':
        raise VerificationFailure('evidence directory must be outside repositories')
    path.mkdir(parents=True, exist_ok=True)
    return path


def main():
    if len(sys.argv) != 2:
        raise VerificationFailure('supply a private evidence directory')
    scenario = os.environ.get('ASSISTANT_LIVE_SCENARIO', 'reading')
    if scenario not in ('reading', 'recipe'):
        raise VerificationFailure('unknown live scenario')
    for name in ('TELEGRAM_BOT_TOKEN', 'OPENAI_API_KEY', 'OPENAI_MODEL', 'SUBJECT_ID', 'ALLOWED_USERS', 'ALLOWED_CHATS'):
        if not os.environ.get(name):
            raise VerificationFailure('required application configuration unavailable')
    subject = str(uuid.UUID(os.environ['SUBJECT_ID']))
    if subject == str(uuid.UUID(int=0)):
        raise VerificationFailure('synthetic subject unavailable')
    directory = private_directory(sys.argv[1])
    revision = run(['git', '-C', str(ROOT), 'rev-parse', 'HEAD'])
    dirty = run(['git', '-C', str(ROOT), 'status', '--porcelain'])
    if dirty:
        raise VerificationFailure('commit the exact application under test before running')
    run_id = uuid.uuid4().hex
    receipt_path = directory / ('foundation-' + run_id + '.json')
    receipt = {'run_id': run_id, 'revision': revision, 'scenario': scenario, 'outcome': 'unavailable'}
    container = None
    child = None

    def save():
        temp = receipt_path.with_suffix('.tmp')
        temp.write_text(json.dumps(receipt, ensure_ascii=False, indent=2, default=receipt_value), encoding='utf-8')
        temp.replace(receipt_path)

    save()
    try:
        run(['dotnet', 'build', str(ROOT / 'Assistant.slnx'), '-c', 'Release',
             '-warnaserror', '-v', 'q', '-nologo', '--no-incremental'], timeout=180)
        if run(['git', '-C', str(ROOT), 'status', '--porcelain']):
            raise VerificationFailure('checkout changed during build')
        if not DLL.is_file():
            raise VerificationFailure('built application unavailable')
        receipt['binary_sha256'] = hashlib.sha256(DLL.read_bytes()).hexdigest()
        save()
        run(['docker', 'pull', 'postgres:17.6-alpine'], timeout=120)
        container_name = 'assistant-live-' + run_id
        container = run(['docker', 'create', '--name', container_name, '--label', 'assistant-live-run=' + run_id,
                         '-e', 'POSTGRES_PASSWORD=synthetic-only', '-e', 'POSTGRES_DB=runner',
                         '-p', '127.0.0.1::5432', 'postgres:17.6-alpine'], timeout=120)
        run(['docker', 'start', container])
        deadline = time.monotonic() + 60
        while True:
            try:
                run(['docker', 'exec', container, 'pg_isready', '-U', 'postgres'], timeout=3)
                break
            except (subprocess.SubprocessError, OSError):
                if time.monotonic() >= deadline:
                    raise VerificationFailure('isolated database readiness timeout')
                time.sleep(0.5)
        port = run(['docker', 'port', container, '5432/tcp']).rsplit(':', 1)[-1]
        if not port.isdecimal():
            raise VerificationFailure('isolated database port unavailable')
        environment = dict(os.environ, DATABASE_CONNECTION=(
            f'Host=127.0.0.1;Port={port};Database=runner;Username=postgres;Password=synthetic-only'))
        child = Child(environment)
        child.wait_for('polling_ready', 60)
        def restart():
            nonlocal child
            child.stop()
            child = Child(environment)
            child.wait_for('polling_ready', 60)
            return child

        if scenario == 'recipe':
            execute_recipe(container, subject, child, restart, run, witness, receipt, save)
        else:
            if rows(container) != []:
                raise VerificationFailure('test database was not empty')
            source = witness('Send /record 7.8 mmol/L in the selected General topic; enter source witness JSON:')
            receipt['source'] = source
            save()
            child.wait_for('extraction_completed', 10)
            time.sleep(0.2)
            child.drain()
            if child.count != 1:
                raise VerificationFailure('expected exactly one successful extraction')
            observed_rows = rows(container)
            receipt['rows'] = observed_rows
            save()
            child.stop()
            if child.count != 1 or child.requests != 1:
                raise VerificationFailure('expected exactly one extraction request and result')
            child = Child(environment)
            child.wait_for('polling_ready', 60)
            last = witness('Send /last in the same topic; enter linked reply witness JSON:')
            receipt['last'] = last
            save()
            child.stop()
            if child.requests != 0 or child.count != 0:
                raise VerificationFailure('last triggered an extraction')
            check(observed_rows, subject, source, last)
            receipt.update(outcome='pass', source=source, last=last, rows=observed_rows)
    except KeyboardInterrupt:
        receipt.update(outcome='fail', reason='interrupted')
    except Exception as error:
        receipt.update(outcome='fail', reason=str(error) if isinstance(error, VerificationFailure) else type(error).__name__)
    finally:
        if child is not None:
            try:
                child.stop()
            except Exception:
                receipt.update(outcome='fail', cleanup='child cleanup failed')
        if container is None and 'container_name' in locals():
            try:
                identity = run(['docker', 'inspect', '--format',
                                '{{.Id}} {{index .Config.Labels "assistant-live-run"}}', container_name])
                parts = identity.split()
                if len(parts) == 2 and parts[1] == run_id:
                    container = parts[0]
                else:
                    receipt.update(outcome='fail', cleanup='container ownership not confirmed')
            except (subprocess.SubprocessError, OSError):
                receipt.update(outcome='fail', cleanup='container creation outcome could not be confirmed')
        if container is not None:
            try:
                run(['docker', 'rm', '-f', container], timeout=20)
            except Exception:
                receipt.update(outcome='fail', cleanup='container cleanup failed')
        save()
    print('Live outcome:', receipt['outcome'], '; private receipt:', receipt_path, flush=True)
    return 0 if receipt['outcome'] == 'pass' else 1


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception as error:
        print('Live outcome: unavailable;', type(error).__name__, flush=True)
        sys.exit(1)
