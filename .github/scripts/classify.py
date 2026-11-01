import os
import subprocess
import sys


def requires_application_checks(paths):
    if not paths:
        return True
    for path in paths:
        parts = path.replace('\\', '/').split('/')
        if (not path.endswith('.md') or not parts[0]
                or (parts[0] in {'src', 'roles'} and parts[-1] != 'AGENTS.md')):
            return True
    return False


def main():
    base = os.environ.get('CHANGE_BASE', '')
    head = os.environ.get('CHANGE_HEAD', '')
    needs = True
    if base and head and base.strip('0') and head.strip('0'):
        try:
            diff = subprocess.run(
                ['git', 'diff', '--name-only', '-z', '--no-renames', base, head, '--'],
                check=True, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
            if diff.stdout.endswith(b'\0'):
                paths = [os.fsdecode(p) for p in diff.stdout[:-1].split(b'\0')]
                needs = requires_application_checks(paths)
        except (OSError, subprocess.CalledProcessError):
            pass
    with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as output:
        output.write(f"application={'true' if needs else 'false'}\n")


if __name__ == '__main__':
    main()
