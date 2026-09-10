#!/usr/bin/env python3
"""Curated regression mutations, in a disposable copy, never in the working tree.

Run from any directory. Each mutant must compile, then fail an assertion in its
selected test group. Build errors/timeouts are inconclusive, never 'killed'.
"""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import time

root = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--only', help='comma-separated mutation IDs')
parser.add_argument('--output', default=str(root / 'TestResults/test-audit/mutations.json'))
args = parser.parse_args()
specs = json.loads((Path(__file__).with_name('mutations.json')).read_text())
if args.only:
    requested = set(args.only.split(','))
    specs = [s for s in specs if s['id'] in requested]
    if {s['id'] for s in specs} != requested:
        parser.error('unknown mutation ID')
output = Path(args.output).resolve()
output.parent.mkdir(parents=True, exist_ok=True)
results = []
output.write_text("[]\n")


def run(command, cwd, timeout=120):
    try:
        p = subprocess.run(command, cwd=cwd, text=True, stdout=subprocess.PIPE,
                           stderr=subprocess.STDOUT, timeout=timeout)
        return p.returncode, p.stdout
    except subprocess.TimeoutExpired:
        return 124, 'TIMEOUT'


with tempfile.TemporaryDirectory(prefix='kitty-mutations-') as temporary:
    work = Path(temporary)
    shutil.copytree(root / 'src', work / 'src', ignore=shutil.ignore_patterns('bin', 'obj'))
    shutil.copytree(root / 'examples', work / 'examples')
    project = 'src/KiTTYManager.SelfTest/KiTTYManager.SelfTest.csproj'
    assembly = work / 'src/KiTTYManager.SelfTest/bin/Debug/net8.0/KiTTYManager.SelfTest.dll'
    code, log = run(['dotnet', 'build', project, '-v:q', '--nologo'], work)
    if code:
        raise SystemExit('Baseline build failed:\n' + log)
    filters = ','.join(sorted({s['tests'] for s in specs}))
    code, log = run(['dotnet', str(assembly), '--filter', filters], work)
    if code or 'PASS  ' not in log:
        raise SystemExit('Baseline selected tests failed:\n' + log)
    (output.parent / 'mutation-baseline.log').write_text(log)
    for spec in specs:
        path = work / spec['file']
        original = path.read_text()
        if original.count(spec['before']) != 1:
            raise SystemExit('Mutation anchor is not unique: ' + spec['id'])
        started = time.monotonic()
        try:
            path.write_text(original.replace(spec['before'], spec['after'], 1))
            code, log = run(['dotnet', 'build', project, '--no-restore', '-v:q', '--nologo'], work)
            status = 'invalid' if code else 'survived'
            if not code:
                code, log = run(['dotnet', str(assembly), '--filter', spec['tests']], work, 60)
                if code == 124:
                    status = 'timeout'
                elif code != 0 and 'FAIL  ' in log:
                    status = 'killed'
                elif code != 0 or 'PASS  ' not in log:
                    status = 'inconclusive'
            (output.parent / (spec['id'] + '.log')).write_text(log)
            results.append({'id': spec['id'], 'status': status,
                            'tests': spec['tests'], 'seconds': round(time.monotonic() - started, 2)})
            output.write_text(json.dumps(results, ensure_ascii=False, indent=2) + '\n')
            print(spec['id'] + ': ' + status, flush=True)
        finally:
            path.write_text(original)
print('Results: ' + str(output))
raise SystemExit(0 if results and all(r['status'] == 'killed' for r in results) else 1)
