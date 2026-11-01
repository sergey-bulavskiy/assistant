import contextlib
from copy import deepcopy
from decimal import Decimal
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import recipe_scenario
import run as runner
from test_verify_recipe import PREF, PW, ROW, RW, SUBJECT, SW
from verify import VerificationFailure


class Child:
    def __init__(self, extra_request=False):
        self.requests = 0
        self.recipe_requests = int(extra_request)
        self.stopped = False

    def wait_for(self, marker, seconds):
        if marker == 'recipe_requested':
            self.recipe_requests += 1

    def stop(self):
        self.stopped = True


class RecipeScenarioTests(unittest.TestCase):
    def exercise(self, extra_request=False, semantic='pass'):
        queries = iter([[], [], [PREF], [PREF], [ROW]])
        witnesses = iter([PW, RW, {'semantic_check': semantic}, SW])
        before, after = Child(), Child(extra_request)
        receipt = {}

        def query(args):
            return json.dumps(next(queries))

        recipe_scenario.execute('owned', SUBJECT, before, lambda: after, query,
                                lambda prompt: next(witnesses), receipt, lambda: None)
        self.assertTrue(before.stopped)
        self.assertTrue(after.stopped)
        return receipt

    def test_exact_recipe_and_shopping_pass(self):
        receipt = self.exercise()
        self.assertEqual('pass', receipt['outcome'])
        self.assertEqual(ROW, receipt['selected_recipe'])
        self.assertEqual([PREF], receipt['preference_before_restart'])
        self.assertEqual([PREF], receipt['preference_after_restart'])

    def test_shopping_extra_failed_provider_attempt_is_rejected(self):
        with self.assertRaisesRegex(VerificationFailure, 'unexpected OpenAI request count'):
            self.exercise(extra_request=True)

    def test_jsonb_decimal_precision_is_preserved_and_wrong_amount_rejected(self):
        exact = '1.000000000000000000000000001'
        row = deepcopy(ROW)
        row['ingredients'][0]['Grams'] = exact
        raw = json.dumps([row]).replace('"' + exact + '"', exact)
        queries = iter(['[]', '[]', json.dumps([PREF]), json.dumps([PREF]), raw])
        recipe = dict(RW, reply_text=RW['reply_text'].replace('80 г', exact + ' г'))
        shopping = dict(SW, reply_text=SW['reply_text'].replace('80 г', exact + ' г'))
        witnesses = iter([PW, recipe, {'semantic_check': 'pass'}, shopping])
        receipt = {}
        before, after = Child(), Child()
        recipe_scenario.execute('owned', SUBJECT, before, lambda: after, lambda args: next(queries),
                                lambda prompt: next(witnesses), receipt, lambda: None)
        self.assertEqual(Decimal(exact), receipt['selected_recipe']['ingredients'][0]['Grams'])
        encoded = json.dumps(receipt, default=runner.receipt_value)
        self.assertEqual(exact, json.loads(encoded)['selected_recipe']['ingredients'][0]['Grams'])
        wrong = dict(shopping, reply_text=shopping['reply_text'].replace(exact, '1.000000000000000000000000002'))
        with self.assertRaises(VerificationFailure):
            recipe_scenario.check_shopping(receipt['selected_recipe'], recipe, wrong)

    def test_failed_semantic_check_is_rejected(self):
        with self.assertRaisesRegex(VerificationFailure, 'semantic inspection'):
            self.exercise(semantic='fail')

    def test_marker_is_counted_after_output_reader_finishes(self):
        process = mock.Mock()
        process.poll.return_value = 0
        process.stdout = io.StringIO('polling_ready\nrecipe_requested\n')
        with mock.patch.object(runner.subprocess, 'Popen', return_value=process):
            child = runner.Child({})
            child.stop()
        self.assertEqual(1, child.recipe_requests)

    def test_failure_after_restart_cleans_both_children_and_owned_container(self):
        children = []
        calls = []

        class FakeChild(Child):
            def __init__(self, environment):
                super().__init__()
                children.append(self)

        def fake_run(args, timeout=20):
            calls.append(args)
            if args[0] == 'git':
                return 'abc123' if 'rev-parse' in args else ''
            if args[:2] == ['docker', 'create']:
                return 'owned-container-id'
            if args[:2] == ['docker', 'port']:
                return '127.0.0.1:15432'
            return ''

        def fail_after_restart(container, subject, child, restart, run, witness, receipt, save):
            restart()
            raise VerificationFailure('synthetic failure')

        with tempfile.TemporaryDirectory() as directory:
            dll = Path(directory) / 'Assistant.dll'
            dll.write_bytes(b'synthetic-binary')
            env = {name: 'synthetic' for name in ('TELEGRAM_BOT_TOKEN', 'OPENAI_API_KEY', 'OPENAI_MODEL',
                                                    'ALLOWED_USERS', 'ALLOWED_CHATS')}
            env.update(SUBJECT_ID=SUBJECT, ASSISTANT_LIVE_SCENARIO='recipe')
            with mock.patch.dict(os.environ, env), mock.patch.object(runner, 'DLL', dll), \
                 mock.patch.object(runner, 'private_directory', return_value=Path(directory)), \
                 mock.patch.object(runner, 'run', side_effect=fake_run), \
                 mock.patch.object(runner, 'Child', FakeChild), \
                 mock.patch.object(runner, 'execute_recipe', side_effect=fail_after_restart), \
                 mock.patch.object(runner.sys, 'argv', ['run.py', directory]), \
                 contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(1, runner.main())
            self.assertEqual(2, len(children))
            self.assertTrue(all(child.stopped for child in children))
            self.assertEqual([['docker', 'rm', '-f', 'owned-container-id']],
                             [item for item in calls if item[:2] == ['docker', 'rm']])
            receipt = json.loads(next(Path(directory).glob('foundation-*.json')).read_text(encoding='utf-8'))
            self.assertEqual(('fail', 'synthetic failure', 'recipe'),
                             (receipt['outcome'], receipt['reason'], receipt['scenario']))


if __name__ == '__main__':
    unittest.main()
