import contextlib
from decimal import Decimal
import io
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock

import run as runner
from test_verify import LAST, ROW, SOURCE, SUBJECT
from verify import VerificationFailure


class FakeChild:
    instances = []
    request_during_last = False
    def __init__(self, environment):
        self.count = 0
        self.requests = int(FakeChild.request_during_last and bool(FakeChild.instances))
        self.stops = 0
        FakeChild.instances.append(self)
    def wait_for(self, marker, seconds):
        if marker == 'extraction_completed':
            self.count += 1
            self.requests += 1
    def drain(self):
        pass
    def stop(self):
        self.stops = 1


class RunnerCleanupTests(unittest.TestCase):
    def test_rows_parser_rejects_difference_beyond_float_precision(self):
        raw = '[{"subject_id":"11111111-1111-1111-1111-111111111111","bot_id":111,' \
              '"chat_id":222,"message_id":333,"topic_id":null,"value":7.8000000000000001,"unit":"mmol/L"}]'
        with mock.patch.object(runner, 'run', return_value=raw):
            rows = runner.rows('synthetic-container')
        self.assertEqual(Decimal('7.8000000000000001'), rows[0]['value'])
        with self.assertRaises(VerificationFailure):
            runner.check(rows, SUBJECT, SOURCE, LAST)

    def exercise(self, second_witness, request_during_last=False):
        FakeChild.instances = []
        FakeChild.request_during_last = request_during_last
        calls = []
        def fake_run(args, timeout=20):
            calls.append(args)
            if args[0] == 'git':
                return 'abc123' if 'rev-parse' in args else ''
            if args[:2] == ['docker', 'create']:
                return 'owned-container-id'
            if args[:2] == ['docker', 'port']:
                return '127.0.0.1:15432'
            return ''
        with tempfile.TemporaryDirectory() as directory:
            dll = Path(directory) / 'Assistant.dll'
            dll.write_bytes(b'synthetic-binary')
            env = {name: 'synthetic' for name in ('TELEGRAM_BOT_TOKEN', 'OPENAI_API_KEY', 'OPENAI_MODEL',
                                                    'ALLOWED_USERS', 'ALLOWED_CHATS')}
            env['SUBJECT_ID'] = SUBJECT
            with mock.patch.dict(os.environ, env), mock.patch.object(runner, 'DLL', dll), \
                 mock.patch.object(runner, 'private_directory', return_value=Path(directory)), \
                 mock.patch.object(runner, 'run', side_effect=fake_run), \
                 mock.patch.object(runner, 'rows', side_effect=[[], [ROW]]), \
                 mock.patch.object(runner, 'Child', FakeChild), \
                 mock.patch.object(runner, 'witness', side_effect=[SOURCE, second_witness]), \
                 mock.patch.object(runner.time, 'sleep'), \
                 mock.patch.object(runner.sys, 'argv', ['run.py', directory]), \
                 contextlib.redirect_stdout(io.StringIO()):
                result = runner.main()
            self.assertEqual([['docker', 'rm', '-f', 'owned-container-id']],
                             [item for item in calls if item[:2] == ['docker', 'rm']])
            self.assertTrue(FakeChild.instances)
            self.assertTrue(all(child.stops == 1 for child in FakeChild.instances))
            return result

    def test_stop_joins_reader_and_counts_all_markers(self):
        process = mock.Mock()
        process.poll.return_value = 0
        process.stdout = io.StringIO('polling_ready\nextraction_requested\nextraction_completed\n')
        with mock.patch.object(runner.subprocess, 'Popen', return_value=process):
            child = runner.Child({})
            child.stop()
        self.assertEqual(1, child.requests)
        self.assertEqual(1, child.count)
    def test_success_cleans_owned_resources(self):
        self.assertEqual(0, self.exercise(LAST))

    def test_failed_witness_cleans_owned_resources(self):
        self.assertEqual(1, self.exercise(VerificationFailure('synthetic failure')))

    def test_interruption_cleans_owned_resources(self):
        self.assertEqual(1, self.exercise(KeyboardInterrupt()))

    def test_last_request_even_without_success_marker_fails(self):
        self.assertEqual(1, self.exercise(LAST, request_during_last=True))

    def test_create_timeout_recovers_only_container_with_own_label(self):
        calls = []
        run_id = None

        def fake_run(args, timeout=20):
            nonlocal run_id
            calls.append(args)
            if args[0] == 'git':
                return 'abc123' if 'rev-parse' in args else ''
            if args[:2] == ['docker', 'create']:
                run_id = args[args.index('--label') + 1].split('=', 1)[1]
                raise subprocess.TimeoutExpired(args, timeout)
            if args[:2] == ['docker', 'inspect']:
                return 'owned-container-id ' + run_id
            return ''

        with tempfile.TemporaryDirectory() as directory:
            dll = Path(directory) / 'Assistant.dll'
            dll.write_bytes(b'synthetic-binary')
            env = {name: 'synthetic' for name in ('TELEGRAM_BOT_TOKEN', 'OPENAI_API_KEY', 'OPENAI_MODEL',
                                                    'ALLOWED_USERS', 'ALLOWED_CHATS')}
            env['SUBJECT_ID'] = SUBJECT
            with mock.patch.dict(os.environ, env), mock.patch.object(runner, 'DLL', dll), \
                 mock.patch.object(runner, 'private_directory', return_value=Path(directory)), \
                 mock.patch.object(runner, 'run', side_effect=fake_run), \
                 mock.patch.object(runner.sys, 'argv', ['run.py', directory]), \
                 contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(1, runner.main())
            self.assertEqual([['docker', 'rm', '-f', 'owned-container-id']],
                             [item for item in calls if item[:2] == ['docker', 'rm']])

    def test_failed_build_does_not_start_docker_even_if_old_binary_exists(self):
        calls = []

        def fake_run(args, timeout=20):
            calls.append(args)
            if args[0] == 'git':
                return 'abc123' if 'rev-parse' in args else ''
            if args[:2] == ['dotnet', 'build']:
                raise subprocess.CalledProcessError(1, args)
            raise AssertionError('unexpected command')

        with tempfile.TemporaryDirectory() as directory:
            dll = Path(directory) / 'Assistant.dll'
            dll.write_bytes(b'stale-binary')
            env = {name: 'synthetic' for name in ('TELEGRAM_BOT_TOKEN', 'OPENAI_API_KEY', 'OPENAI_MODEL',
                                                    'ALLOWED_USERS', 'ALLOWED_CHATS')}
            env['SUBJECT_ID'] = SUBJECT
            with mock.patch.dict(os.environ, env), mock.patch.object(runner, 'DLL', dll), \
                 mock.patch.object(runner, 'private_directory', return_value=Path(directory)), \
                 mock.patch.object(runner, 'run', side_effect=fake_run), \
                 mock.patch.object(runner.sys, 'argv', ['run.py', directory]), \
                 contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(1, runner.main())
            self.assertFalse(any(args[0] == 'docker' for args in calls))


if __name__ == '__main__':
    unittest.main()
