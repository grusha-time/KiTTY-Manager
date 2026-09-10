"""Compact JSON stdout callback for the KiTTY Manager serial bridge."""
import json

from ansible.plugins.callback import CallbackBase


class CallbackModule(CallbackBase):
    CALLBACK_VERSION = 2.0
    CALLBACK_TYPE = "stdout"
    CALLBACK_NAME = "kitty_manager"

    def emit(self, kind, **values):
        self._display.display("KITTY_EVENT:" + json.dumps(
            {"kind": kind, **values}, ensure_ascii=False, separators=(",", ":")))

    def v2_playbook_on_start(self, playbook):
        self.emit("playbook", name=playbook._file_name)

    def v2_playbook_on_play_start(self, play):
        self.emit("play", name=play.get_name().strip())

    def v2_playbook_on_task_start(self, task, is_conditional):
        self.emit("task", name=task.get_name().strip(), action=task.action)

    def result(self, kind, result, **values):
        self.emit(kind, host=result._host.get_name(),
                  result=json.loads(self._dump_results(result._result)), **values)

    def v2_runner_on_ok(self, result):
        self.result("ok", result, changed=bool(result._result.get("changed")))

    def v2_runner_on_failed(self, result, ignore_errors=False):
        self.result("failed", result, ignoreErrors=bool(ignore_errors))

    def v2_runner_on_unreachable(self, result):
        self.result("unreachable", result)

    def v2_runner_on_skipped(self, result):
        self.result("skipped", result)

    def v2_playbook_on_stats(self, stats):
        self.emit("stats", hosts={host: stats.summarize(host) for host in sorted(stats.processed)})
