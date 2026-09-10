#!/usr/bin/env python3
"""Single-run serial agent for KiTTY Manager's immutable Ansible VM."""
import base64
import json
import os
import pathlib
import shutil
import signal
import stat
import subprocess
import sys
import tempfile
import termios
import threading
import time
import zipfile

SERIAL = "/dev/ttyS1"
current = None
worker = None
stop_requested = threading.Event()
serial_lock = threading.Lock()
uploads = {}

def configure_serial(stream):
    attributes = termios.tcgetattr(stream.fileno())
    attributes[3] &= ~(termios.ECHO | termios.ECHONL | termios.ICANON)
    attributes[6][termios.VMIN] = 1
    attributes[6][termios.VTIME] = 0
    termios.tcsetattr(stream.fileno(), termios.TCSANOW, attributes)

TRANSIENT_SSH_FAILURES = (
    "connection refused", "connection timed out", "operation timed out",
    "no route to host", "network is unreachable", "connection reset",
    "connection closed by", "connection aborted", "broken pipe",
)
PERMANENT_SSH_FAILURES = (
    "permission denied", "no supported authentication methods",
    "host key verification failed", "bad configuration option",
    "unknown option", "illegal option", "invalid port", "could not resolve hostname",
)

def retryable_ssh_failure(debug_text, authenticated):
    lowered = debug_text.lower()
    if any(marker in lowered for marker in PERMANENT_SSH_FAILURES):
        return False
    return any(marker in lowered for marker in TRANSIENT_SSH_FAILURES)

def terminate_process_group(process):
    if process and process.poll() is None:
        try:
            os.killpg(process.pid, signal.SIGTERM)
        except ProcessLookupError:
            pass

def build_ssh_wrapper(lost_path, debug_path, ask_environment):
    return ("#!/usr/bin/env python3\n" +
        "import os, pathlib, subprocess, sys, time\n" +
        f"lost=pathlib.Path({str(lost_path)!r}); debug=pathlib.Path({str(debug_path)!r})\n" +
        f"env=os.environ.copy(); env.update({ask_environment!r})\n" +
        f"transient={TRANSIENT_SSH_FAILURES!r}; permanent={PERMANENT_SSH_FAILURES!r}\n" +
        "while True:\n" +
        "    started=time.monotonic(); debug.unlink(missing_ok=True)\n" +
        "    process=subprocess.Popen(['/usr/bin/ssh','-v','-E',str(debug)," +
        "'-o','PreferredAuthentications=publickey,keyboard-interactive,password'," +
        "'-o','KbdInteractiveAuthentication=yes','-o','PasswordAuthentication=yes',*sys.argv[1:]], env=env)\n" +
        "    authenticated=False\n" +
        "    while process.poll() is None:\n" +
        "        if not authenticated and debug.exists():\n" +
        "            authenticated='Authenticated to ' in debug.read_text(encoding='utf-8', errors='ignore')\n" +
        "            if authenticated: lost.unlink(missing_ok=True)\n" +
        "        time.sleep(0.1)\n" +
        "    code=process.wait()\n" +
        "    raw=debug.read_text(encoding='utf-8', errors='ignore') if debug.exists() else ''\n" +
        "    text=raw.lower()\n" +
        "    retry=code == 255 and not any(x in text for x in permanent) and any(x in text for x in transient)\n" +
        "    if not retry: lost.unlink(missing_ok=True); sys.stderr.write(raw[-8192:]); raise SystemExit(code)\n" +
        "    lost.touch(exist_ok=True)\n" +
        "    time.sleep(max(0.0, 10.0-(time.monotonic()-started)))\n")

def emit(kind, **values):
    line = json.dumps({"type": kind, **values}, ensure_ascii=False, separators=(",", ":"))
    with serial_lock:
        serial.write(line + "\n"); serial.flush()

def write_secret(path, value):
    path.write_text(value, encoding="utf-8")
    path.chmod(stat.S_IRUSR | stat.S_IWUSR)

def secret_command(value):
    encoded = base64.b64encode(value.encode("utf-8")).decode("ascii")
    return "printf '%s' '" + encoded + "' | base64 -d"

def yaml_quote(value):
    return "'" + value.replace("'", "''").replace("\r", "\\r").replace("\n", "\\n") + "'"

def safe_extract(archive_path, destination):
    archive = destination / "task.zip"
    shutil.move(str(archive_path), archive)
    with zipfile.ZipFile(archive) as source:
        root = destination.resolve()
        for item in source.infolist():
            target = (destination / item.filename).resolve()
            if root not in target.parents and target != root:
                raise ValueError("unsafe task archive path")
        source.extractall(destination / "task")

def emit_downloads(root):
    if not root.exists(): return
    for path in sorted(item for item in root.rglob("*") if item.is_file()):
        relative = path.relative_to(root).as_posix()
        emit("download_start", relativePath=relative)
        with path.open("rb") as source:
            while True:
                chunk = source.read(256 * 1024)
                if not chunk: break
                emit("download_chunk", relativePath=relative,
                     dataBase64=base64.b64encode(chunk).decode("ascii"))
        emit("download_end", relativePath=relative)

def monitor_connection_status(root, names, stopped):
    known = set()
    while not stopped.wait(0.5):
        current_status = {path.stem for path in root.glob("*.lost")}
        for key in sorted(current_status - known):
            emit("connection_lost", message=names.get(key, key))
        for key in sorted(known - current_status):
            emit("connection_recovered", message=names.get(key, key))
        known = current_status

def run(request, archive_path):
    global current
    work = pathlib.Path(tempfile.mkdtemp(prefix="kitty-ansible-"))
    agent = None
    secrets = []
    connection_monitor_stop = threading.Event()
    connection_monitor = None
    try:
        safe_extract(archive_path, work)
        task = work / "task"
        download_root = work / "downloads"; download_root.mkdir(mode=0o700)
        secret_root = work / "secrets"; secret_root.mkdir(mode=0o700)
        inventory = secret_root / "inventory.yml"
        inventory_text = request["inventory"]
        inventory_text = inventory_text.replace("__KITTY_UPLOAD_DIR__", str(task / ".kitty-uploads"))
        inventory_text = inventory_text.replace("__KITTY_DOWNLOAD_DIR__", str(download_root))
        host_vars = secret_root / "host_vars"; host_vars.mkdir(mode=0o700)
        connection_status = work / "connection-status"; connection_status.mkdir(mode=0o700)
        connection_names = {}
        env = os.environ.copy()
        env.update({"ANSIBLE_NOCOLOR": "1", "ANSIBLE_FORCE_COLOR": "0", "ANSIBLE_HOST_KEY_CHECKING": "False",
                    "ANSIBLE_STDOUT_CALLBACK": "kitty_manager",
                    "ANSIBLE_CALLBACK_PLUGINS": "/usr/local/share/kitty-ansible/callback_plugins",
                    "ANSIBLE_SSH_RETRIES": "0"})
        agent = subprocess.Popen(["ssh-agent", "-s"], stdout=subprocess.PIPE, text=True)
        agent_text = agent.communicate(timeout=5)[0]
        for line in agent_text.splitlines():
            if line.startswith("SSH_AUTH_SOCK=") or line.startswith("SSH_AGENT_PID="):
                key, value = line.split(";", 1)[0].split("=", 1); env[key] = value
        for index, host in enumerate(request.get("hosts", [])):
            ask_path = None
            if host.get("privateKey"):
                key_path = secret_root / f"key-{index}"
                write_secret(key_path, base64.b64decode(host["privateKey"]).decode("utf-8"))
                if key_path.read_text(encoding="utf-8", errors="ignore").startswith("PuTTY-User-Key-File-"):
                    converted = secret_root / f"key-openssh-{index}"
                    command = ["puttygen", str(key_path), "-O", "private-openssh-new", "-o", str(converted)]
                    if host.get("privateKeyPassphrase"):
                        old_pass = secret_root / f"putty-pass-{index}"
                        write_secret(old_pass, host["privateKeyPassphrase"] + "\n")
                        command += ["--old-passphrase", str(old_pass)]
                    empty_pass = secret_root / f"putty-empty-{index}"
                    write_secret(empty_pass, "\n"); command += ["--new-passphrase", str(empty_pass)]
                    subprocess.run(command, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, check=True)
                    key_path = converted
                add_env = env.copy()
                if host.get("privateKeyPassphrase"):
                    ask = secret_root / f"key-askpass-{index}.sh"
                    write_secret(ask, "#!/bin/sh\n" + secret_command(host["privateKeyPassphrase"]) + "\nprintf '\\n'\n")
                    ask.chmod(0o700); add_env.update({"SSH_ASKPASS": str(ask), "SSH_ASKPASS_REQUIRE": "force", "DISPLAY": ":0"})
                subprocess.run(["ssh-add", str(key_path)], env=add_env, stdin=subprocess.DEVNULL,
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True)
            if host.get("password"):
                ask = secret_root / f"password-{index}.sh"
                write_secret(ask, "#!/bin/sh\n" + secret_command(host["password"]) + "\nprintf '\\n'\n"); ask.chmod(0o700)
                ask_path = str(ask)
            wrapper = secret_root / f"ssh-{index}.sh"
            lost_path = connection_status / f"{index}.lost"
            debug_path = connection_status / f"{index}.ssh.log"
            connection_names[str(index)] = host.get("inventoryName", f"host-{index}")
            ask_environment = ({"SSH_ASKPASS": ask_path, "SSH_ASKPASS_REQUIRE": "force", "DISPLAY": ":0"}
                               if ask_path else {})
            write_secret(wrapper, build_ssh_wrapper(lost_path, debug_path, ask_environment))
            wrapper.chmod(0o700)
            inventory_text = inventory_text.replace(f"__KITTY_SSH_{index}__", str(wrapper))
            if host.get("rootPassword"):
                name = host.get("inventoryName", "")
                if not name or "/" in name or "\\" in name or name in (".", ".."):
                    raise ValueError("unsafe inventory host name")
                write_secret(host_vars / (name + ".yml"), "ansible_become_password: " + yaml_quote(host["rootPassword"]) + "\n")
        inventory.write_text(inventory_text, encoding="utf-8")
        if request.get("vaultPassword"):
            vault = secret_root / "vault.sh"
            write_secret(vault, "#!/bin/sh\n" + secret_command(request["vaultPassword"]) + "\nprintf '\\n'\n"); vault.chmod(0o700)
        playbook = (task / request["playbookRelativePath"]).resolve()
        if task.resolve() not in playbook.parents or not playbook.is_file(): raise ValueError("unsafe playbook path")
        command = ["ansible-playbook", str(playbook), "-i", str(inventory), "--forks", "5"]
        verbosity = max(0, min(4, int(request.get("verbosity", 0))))
        if verbosity: command.append("-" + "v" * verbosity)
        if request.get("vaultPassword"): command += ["--vault-password-file", str(secret_root / "vault.sh")]
        connection_monitor = threading.Thread(target=monitor_connection_status,
            args=(connection_status, connection_names, connection_monitor_stop), daemon=True)
        connection_monitor.start()
        emit("started", command="ansible-playbook", forks=5)
        if stop_requested.is_set(): raise InterruptedError("stopped before ansible-playbook start")
        current = subprocess.Popen(command, cwd=task, env=env, text=True, stdout=subprocess.PIPE,
                                   stderr=subprocess.STDOUT, bufsize=1, start_new_session=True)
        if stop_requested.is_set(): terminate_process_group(current)
        for line in current.stdout:
            text = line.rstrip("\r\n")
            if text.startswith("KITTY_EVENT:"):
                emit("callback", text=text[len("KITTY_EVENT:"):])
            else: emit("output", text=text)
        code = current.wait(); emit_downloads(download_root); emit("completed", exitCode=code)
    except Exception as error:
        emit("error", message=str(error), errorType=type(error).__name__)
    finally:
        connection_monitor_stop.set()
        if connection_monitor: connection_monitor.join(timeout=2)
        terminate_process_group(current)
        current = None
        if env.get("SSH_AGENT_PID"):
            subprocess.run(["ssh-agent", "-k"], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        shutil.rmtree(work, ignore_errors=True)

def self_test():
    assert retryable_ssh_failure("Authenticated to target\nConnection reset by peer", True)
    assert not retryable_ssh_failure("Authenticated to target\nExit status 255", True)
    assert retryable_ssh_failure("connect to host 127.0.0.1 port 50000: Connection refused", False)
    assert not retryable_ssh_failure("Permission denied (publickey,password).", False)
    assert not retryable_ssh_failure("Bad configuration option: broken", False)
    assert not retryable_ssh_failure("unclassified ssh failure", False)
    wrapper = build_ssh_wrapper(pathlib.Path("lost"), pathlib.Path("debug"), {})
    assert "any(x in text for x in transient)" in wrapper and "10.0-(time.monotonic()-started)" in wrapper
    assert "PasswordAuthentication=yes" in wrapper and "sys.stderr.write(raw[-8192:])" in wrapper
    child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"], start_new_session=True)
    terminate_process_group(child)
    assert child.wait(timeout=2) == -signal.SIGTERM
    master, slave = os.openpty()
    try:
        with os.fdopen(slave, "r", encoding="utf-8", closefd=False) as test_serial:
            configure_serial(test_serial)
            flags = termios.tcgetattr(test_serial.fileno())[3]
            assert not flags & termios.ECHO and not flags & termios.ECHONL and not flags & termios.ICANON
    finally:
        os.close(master); os.close(slave)

if len(sys.argv) == 2 and sys.argv[1] == "--self-test":
    self_test()
    raise SystemExit(0)

with open(SERIAL, "r", encoding="utf-8", buffering=1) as reader, open(SERIAL, "w", encoding="utf-8", buffering=1) as serial:
    configure_serial(reader)
    emit("ready", ansibleVersion=subprocess.run(["ansible-playbook", "--version"], text=True,
         stdout=subprocess.PIPE, stderr=subprocess.STDOUT).stdout.splitlines()[0])
    for line in reader:
        if not line.strip():
            continue
        try:
            request = json.loads(line)
            if request.get("action") == "run":
                if worker and worker.is_alive(): emit("error", message="Ansible run is already active")
                else:
                    upload_id = request.get("taskUploadId", "")
                    archive_path = uploads.pop(upload_id, None)
                    if not archive_path: raise ValueError("task upload is missing")
                    stop_requested.clear()
                    worker = threading.Thread(target=run, args=(request, archive_path), daemon=True)
                    worker.start()
            elif request.get("action") == "upload_start":
                upload_id = request.get("uploadId", "")
                if not upload_id or upload_id in uploads: raise ValueError("invalid task upload id")
                file = tempfile.NamedTemporaryFile(prefix="kitty-upload-", suffix=".zip", delete=False)
                file.close(); uploads[upload_id] = pathlib.Path(file.name)
            elif request.get("action") == "upload_chunk":
                upload_id = request.get("uploadId", "")
                if upload_id not in uploads: raise ValueError("unknown task upload id")
                with uploads[upload_id].open("ab") as target:
                    target.write(base64.b64decode(request.get("dataBase64", ""), validate=True))
            elif request.get("action") == "upload_end":
                if request.get("uploadId", "") not in uploads: raise ValueError("unknown task upload id")
            elif request.get("action") == "stop":
                stop_requested.set()
                terminate_process_group(current)
                emit("stopping")
            elif request.get("action") == "shutdown":
                stop_requested.set()
                terminate_process_group(current)
                if worker and worker.is_alive(): worker.join(timeout=10)
                emit("shutdown"); subprocess.run(["poweroff", "-f"]); break
        except Exception as error: emit("error", message=str(error), errorType=type(error).__name__)
    for path in uploads.values():
        try: path.unlink(missing_ok=True)
        except OSError: pass
