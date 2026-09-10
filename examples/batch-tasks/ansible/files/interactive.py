#!/usr/bin/env python3
"""Wait for a prompt before replying; this is a target process, not the manager's shell."""
import os
import selectors
import subprocess
import sys
import time

program = "value = input('Identifiers:'); assert value == '17 29'; print('response-ok')"
process = subprocess.Popen([sys.executable, '-u', '-c', program],
                           stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                           stderr=subprocess.STDOUT)
try:
    observed = b''
    deadline = time.monotonic() + 10
    with selectors.DefaultSelector() as selector:
        selector.register(process.stdout, selectors.EVENT_READ)
        while not observed.endswith(b'Identifiers:'):
            remaining = deadline - time.monotonic()
            assert remaining > 0 and selector.select(remaining), 'prompt timeout'
            char = os.read(process.stdout.fileno(), 1)
            assert char, 'process ended before prompt'
            observed += char
    remainder, _ = process.communicate(b'17 29\n', timeout=10)
    assert process.returncode == 0 and remainder == b'response-ok\n', remainder
finally:
    if process.poll() is None:
        process.kill()
    process.wait()
print('prompt-and-response-ok')
