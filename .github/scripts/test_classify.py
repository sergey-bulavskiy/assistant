import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from classify import requires_application_checks


SCRIPT = Path(__file__).with_name('classify.py').resolve()


class ClassificationTests(unittest.TestCase):
    def test_documentation_and_local_guides_skip_application_checks(self):
        self.assertFalse(requires_application_checks([
            'README.md', 'guide/intro.md', '.github/AGENTS.md',
            'tests/AGENTS.md', 'src/Assistant/AGENTS.md', 'roles/AGENTS.md',
        ]))

    def test_application_inputs_and_unknown_files_require_checks(self):
        for path in ['src/Assistant/Program.cs', 'src/input.md', 'roles/prompt.md',
                     'tests/example.cs', '.github/workflows/ci.yml', 'Dockerfile',
                     'Directory.Packages.props', 'unknown.dat']:
            with self.subTest(path=path):
                self.assertTrue(requires_application_checks([path]))

    def test_mixed_and_empty_changes_require_checks(self):
        self.assertTrue(requires_application_checks(['README.md', 'Dockerfile']))
        self.assertTrue(requires_application_checks([]))


class GitBoundaryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name)
        self.git('init', '-q')
        self.write('README.md', 'initial documentation\n')
        self.write('src/input.md', 'runtime content\n')
        self.git('add', '.')
        self.commit('baseline')
        self.base = self.git('rev-parse', 'HEAD').strip()

    def git(self, *args):
        return subprocess.run(
            ['git', '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', *args],
            cwd=self.repo, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True).stdout

    def write(self, path, content):
        target = self.repo / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content, encoding='utf-8')

    def commit(self, message):
        self.git('commit', '-q', '-m', message)

    def classify(self, base=None, head='HEAD'):
        output = self.repo / 'result'
        output.write_text('', encoding='utf-8')
        env = dict(os.environ, CHANGE_BASE=self.base if base is None else base,
                   CHANGE_HEAD=head, GITHUB_OUTPUT=str(output))
        subprocess.run([sys.executable, '-B', str(SCRIPT)], cwd=self.repo,
                       env=env, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        return output.read_text(encoding='utf-8')

    def test_docs_only_diff_skips_checks(self):
        self.write('README.md', 'changed documentation\n')
        self.git('add', 'README.md')
        self.commit('docs')
        self.assertEqual('application=false\n', self.classify())

    def test_code_and_mixed_diffs_require_checks(self):
        self.write('src/Program.cs', 'code\n')
        self.git('add', 'src/Program.cs')
        self.commit('code')
        self.assertEqual('application=true\n', self.classify())
        self.write('README.md', 'changed documentation\n')
        self.git('add', 'README.md')
        self.commit('mixed')
        self.assertEqual('application=true\n', self.classify())

    def test_rename_from_runtime_markdown_to_docs_requires_checks(self):
        self.git('mv', 'src/input.md', 'moved.md')
        self.commit('move runtime input')
        self.assertEqual('application=true\n', self.classify())

    def test_rename_from_docs_to_runtime_markdown_requires_checks(self):
        self.git('mv', 'README.md', 'src/moved.md')
        self.commit('move docs')
        self.assertEqual('application=true\n', self.classify())

    def test_deletion_of_runtime_input_requires_checks(self):
        self.git('rm', '-q', 'src/input.md')
        self.commit('delete runtime input')
        self.assertEqual('application=true\n', self.classify())

    def test_unknown_or_empty_base_and_empty_diff_require_checks(self):
        for base in ('0' * 40, '', 'not-a-revision', self.base):
            with self.subTest(base=base):
                self.assertEqual('application=true\n', self.classify(base=base))


if __name__ == '__main__':
    unittest.main()
