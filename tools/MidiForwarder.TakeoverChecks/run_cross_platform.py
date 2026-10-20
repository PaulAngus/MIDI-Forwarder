"""Exercise immutable Windows/Mac relay assemblies over direct cross-host TCP.

Uses production confirmation windows with Avalonia Headless. Server hardware and
Windows app MIDI are injected fixtures; the Mac client uses temporary CoreMIDI
compatibility endpoints, so the remote host must be the development VM.
"""
import argparse
import hashlib
import json
from pathlib import Path
import queue
import re
import shlex
import subprocess
import threading
import time
import traceback


class Worker:
    def __init__(self, name, command, output):
        self.name = name
        self.lines = queue.Queue()
        self.log = (output / (name + ".jsonl")).open("w", encoding="utf-8")
        self.errors = (output / (name + ".stderr.log")).open("w", encoding="utf-8")
        self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=self.errors, text=True, encoding="utf-8", bufsize=1)

        def reader():
            for line in self.process.stdout:
                self.log.write(line)
                self.log.flush()
                if line.startswith("RESULT "):
                    self.lines.put(json.loads(line[7:]))
            self.lines.put({"ok": False, "error": "Worker exited"})

        threading.Thread(target=reader, daemon=True).start()
        self.identity = self.lines.get(timeout=60)
        assert self.identity.get("ready"), self.identity

    def call(self, op, **kwargs):
        self.process.stdin.write(json.dumps(dict(op=op, **kwargs)) + "\n")
        self.process.stdin.flush()
        reply = self.lines.get(timeout=25)
        if not reply.get("ok"):
            raise RuntimeError(self.name + ": " + reply.get("error", str(reply)))
        assert reply.get("op") == op, reply
        return reply["result"]

    def status(self):
        return self.call("status")

    def wait(self, predicate, description):
        end = time.monotonic() + 15
        state = None
        while time.monotonic() < end:
            state = self.status()
            if predicate(state):
                return state
            time.sleep(0.1)
        raise AssertionError(f"{self.name}: timed out waiting for {description}: {state}")

    def close(self):
        if self.process.poll() is None:
            try:
                result = self.call("quit")
                assert result["portsRestored"], result
                self.process.wait(timeout=15)
            except Exception:
                self.process.stdin.close()
                try:
                    self.process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    self.process.kill()
                raise
        self.log.close()
        self.errors.close()


def main():
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument("--paths", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--vm-host", required=True)
    parser.add_argument("--trusted-host", required=True)
    parser.add_argument("--key", required=True)
    parser.add_argument("--vm-root", required=True)
    parser.add_argument("--distribution-record", type=Path, required=True)
    parser.add_argument("--windows-address", default="192.168.2.10")
    parser.add_argument("--port", type=int, default=52180)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    paths = json.loads(args.paths.read_text(encoding="utf-8-sig"))
    distribution = json.loads(args.distribution_record.read_text(encoding="utf-8-sig"))
    workers = {}
    results = []
    cleanup_errors = []
    try:
        workers["Windows"] = Worker("windows", ["dotnet", str(Path(paths["Root"]) / "windows/MidiForwarder.TakeoverChecks.dll"),
            paths["ClientPayload"], paths["ServerPayload"], "--development-check"], args.output)
        root = args.vm_root
        client_payload = root + "/verified/MIDI Forwarder Client.app/Contents/MacOS"
        server_payload = root + "/verified/MIDI Forwarder Server.app/Contents/MacOS"
        remote = shlex.join(["/usr/local/share/dotnet/dotnet", root + "/takeover-checks/MidiForwarder.TakeoverChecks.dll",
                             client_payload, server_payload, "--development-check"])
        workers["Mac"] = Worker("mac", ["ssh", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o",
            "HostKeyAlias=" + args.trusted_host, "-i", args.key, "paulangus@" + args.vm_host, remote], args.output)
        mac_record = Path(distribution["MacDistributionLog"]).read_text(encoding="utf-8-sig")
        expected_mac = dict(("MidiForwarder." + role, digest.lower()) for digest, role in
                           re.findall(r"([0-9a-fA-F]{64})\s+[^\r\n]+/MidiForwarder\.(Client|Server)\.dll", mac_record))
        assert len(expected_mac) == 2, "Missing final Mac distribution assembly identities"
        for item in workers["Mac"].identity["assemblies"]:
            if item["name"] in expected_mac:
                assert item["sha256"].lower() == expected_mac[item["name"]], "Mac assembly differs from distributed app"
        for item in workers["Windows"].identity["assemblies"]:
            source = Path(paths["ServerPayload"] if item["name"] == "MidiForwarder.Server" else paths["ClientPayload"]) / (item["name"] + ".dll")
            assert hashlib.sha256(source.read_bytes()).hexdigest() == item["sha256"].lower()
        for role in ("Client", "Server"):
            payload = Path(paths[role + "Payload"])
            checked_exe = payload.parents[2] / ("MidiForwarder." + role) / ("MidiForwarder." + role + ".exe")
            expected = next(item["SHA256"] for item in distribution["WindowsCopies"]
                            if Path(item["File"]).name == "MidiForwarder." + role + ".exe")
            assert hashlib.sha256(checked_exe.read_bytes()).hexdigest() == expected.lower(), "Windows extraction differs from distributed EXE"

        packets = [bytes.fromhex("C005"), bytes.fromhex("F8"), bytes([0xF0, 0x7D]) + bytes(i % 128 for i in range(3079)) + bytes([0xF7])]

        def transfer(client, server, checks):
            for packet in packets:
                data = packet.hex().upper()
                client.call("send-client", data=data)
                assert server.call("read-server")["data"] == data, "Client-to-server MIDI mismatch"
                server.call("send-server", data=data)
                assert client.call("read-client")["data"] == data, "Server-to-client MIDI mismatch"
            checks.append("Program Change, clock and 3082-byte SysEx match in both directions")

        for server_name in ("Windows", "Mac"):
            server = workers[server_name]
            for initial_name in ("Windows", "Mac"):
                incoming_name = "Mac" if initial_name == "Windows" else "Windows"
                initial, incoming = workers[initial_name], workers[incoming_name]
                for mode in ("accept", "cancel", "disabled"):
                    checks = []
                    case = dict(server=server_name, initial=initial_name, incoming=incoming_name, allowTakeover=mode != "disabled", action=mode, checks=checks)
                    print("RUN", server_name, initial_name + " -> " + incoming_name, mode, flush=True)
                    try:
                        server.call("start-server", url=f"tcp://0.0.0.0:{args.port}", allow=mode != "disabled")
                        addresses = {
                            name: f"tcp://{'127.0.0.1' if name == server_name else args.windows_address if server_name == 'Windows' else args.vm_host}:{args.port}"
                            for name in workers
                        }
                        initial.call("start-client", url=addresses[initial_name])
                        initial.wait(lambda s: s["client"]["IsConnected"], "initial connection")
                        before = server.wait(lambda s: s["server"]["ConnectedClient"] is not None, "server client admission")["server"]
                        transfer(initial, server, checks)
                        incoming.call("start-client", url=addresses[incoming_name])
                        if mode == "disabled":
                            denied = incoming.wait(lambda s: s["client"]["IsPaused"], "disabled takeover rejection")
                            assert denied["prompt"] is None
                            assert not denied["client"]["IsConnected"]
                            assert "does not allow takeover" in denied["client"]["LastConnectionError"]
                            checks.append("Incoming client paused with busy error and no takeover dialog")
                        else:
                            prompt = incoming.wait(lambda s: s["prompt"] is not None, "production takeover dialog")
                            assert not prompt["client"]["IsConnected"]
                            assert prompt["prompt"]["visible"]
                            assert prompt["prompt"]["title"] == "Take over server connection?"
                            assert "ConfirmTakeoverButton" in [b["Name"] for b in prompt["prompt"]["buttons"]]
                            checks.append("Production dialog visible; incoming client not admitted before confirmation")
                            assert initial.status()["client"]["IsConnected"]
                            transfer(initial, server, checks)
                            pending = server.status()["server"]
                            assert pending["ConnectedClient"] == before["ConnectedClient"]
                            assert pending["opened"] == 1 and pending["released"] == 0
                            incoming.call("confirm", accept=mode == "accept")
                        if mode == "accept":
                            incoming.wait(lambda s: s["client"]["IsConnected"] and s["prompt"] is None, "new client connection")
                            displaced = initial.wait(lambda s: s["client"]["IsPaused"] and not s["client"]["IsConnected"], "displaced client pause")
                            assert "took over" in displaced["client"]["LastConnectionError"]
                            after = server.status()["server"]
                            assert after["opened"] == 2 and after["released"] == 1, after
                            assert after["ConnectedClient"] != before["ConnectedClient"]
                            checks.append("Previous client paused; old MIDI fixture released before opening replacement")
                            transfer(incoming, server, checks)
                            time.sleep(1.5)
                            assert initial.status()["client"]["IsPaused"]
                            assert incoming.status()["client"]["IsConnected"]
                            assert server.status()["server"]["opened"] == 2
                            checks.append("No automatic connection reclaim or repeated prompt")
                        else:
                            if mode == "cancel":
                                incoming.wait(lambda s: s["client"]["IsPaused"] and s["prompt"] is None, "cancelled takeover")
                                checks.append("Cancel dismisses dialog and pauses incoming client")
                            assert initial.status()["client"]["IsConnected"]
                            after = server.status()["server"]
                            assert after["ConnectedClient"] == before["ConnectedClient"]
                            assert after["opened"] == 1 and after["released"] == 0
                            transfer(initial, server, checks)
                            checks.append("Original connection and MIDI ownership unchanged")
                        case["status"] = "passed"
                        print("PASS", server_name, initial_name + " -> " + incoming_name, mode, flush=True)
                    except Exception:
                        case["status"] = "failed"
                        case["error"] = traceback.format_exc()
                        print(case["error"], flush=True)
                    finally:
                        for worker in workers.values():
                            try:
                                cleanup = worker.call("stop-client")
                                assert cleanup["portsRestored"], cleanup
                            except Exception:
                                cleanup_errors.append(traceback.format_exc())
                        try:
                            cleanup = server.call("stop-server")
                            assert cleanup["opened"] == cleanup["released"], cleanup
                        except Exception:
                            cleanup_errors.append(traceback.format_exc())
                        results.append(case)
                    if case["status"] != "passed" or cleanup_errors:
                        raise RuntimeError("Stopped after a failed scenario or cleanup; see report")
    finally:
        for worker in workers.values():
            try:
                worker.close()
            except Exception:
                cleanup_errors.append(traceback.format_exc())
        report = dict(scope=__doc__, machines={name: worker.identity for name, worker in workers.items()},
                      distributionRecord=str(args.distribution_record.resolve()), version=distribution["Version"],
                      cases=results, cleanupErrors=cleanup_errors,
                      passed=len(results) == 12 and all(c["status"] == "passed" for c in results) and not cleanup_errors)
        (args.output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    assert report["passed"], "Incomplete or failed cross-platform matrix"
    print("All 12 cross-platform takeover scenarios passed; test resources restored.", flush=True)


if __name__ == "__main__":
    main()
