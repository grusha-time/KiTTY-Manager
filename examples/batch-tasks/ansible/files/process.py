#!/usr/bin/env python3
"""A tiny target-side program, invoked by the example playbook."""
import hashlib
import json
import os
from pathlib import Path

assert os.environ['KITTY_EXAMPLE'] == 'enabled'
root = Path.cwd()
records = [json.loads((root / (name + '.json')).read_text())
           for name in ['alpha', 'beta', 'gamma']]
result = {'count': len(records), 'sum': sum(item['value'] for item in records),
          'names': [item['name'] for item in records]}
encoded = json.dumps(result, sort_keys=True)
(root / 'result.json').write_text(encoded + '\n')
(root / 'smoke.log').write_text('sha256=' + hashlib.sha256(encoded.encode()).hexdigest() + '\n')
print(encoded)
