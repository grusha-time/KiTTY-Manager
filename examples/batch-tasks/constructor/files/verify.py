"""Verify server-side artifacts; a failed assertion fails the Check step."""
from pathlib import Path
root = Path(__file__).resolve().parent
assert (root / 'payload.txt').read_text() == 'smoke_test=1\nupdated_value=ready\nstatus=ok\n'
backups = list(root.glob('payload.txt.bak.*'))
assert len(backups) == 1, backups
assert 'initial_value=ready' in backups[0].read_text()
assert (root / 'tree/nested/with space.txt').read_text() == 'directory-upload=ok\n'
assert (root / 'second.txt').read_text() == 'second-file=ok\n'
report = (root / 'server.txt').read_text()
assert '{{server.' not in report, report
fields = dict(line.split('=', 1) for line in report.splitlines())
assert set(fields) == {'name', 'host', 'port', 'username'}, fields
assert fields['name'] and fields['host'] and 1 <= int(fields['port']) <= 65535
for name in ['dialog-one.txt', 'dialog-two.txt']:
    assert (root / name).read_text() == 'dialog=ok\n'
assert (root / 'wait.marker').read_text() == 'wait=ok\n'
assert (root / 'command.marker').read_text() == 'command=ok\n'
assert not (root / 'must-not-exist').exists()
(root / 'result.txt').write_text('constructor=PASS\n')
print('CONSTRUCTOR_ARTIFACTS_OK')
