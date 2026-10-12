#!/usr/bin/env python3
"""Runs a command on a pseudo-terminal and types Ctrl+C at it, the way a person would.

    pty_run.py <seconds> [<seconds> ...] -- command...

Each number is a wait before one Ctrl+C. The terminal's line discipline turns the byte into
SIGINT for the whole foreground process group, which is what a real Ctrl+C does and what a
background job started from a script never gets. Prints the output, then the exit status.
"""
import os
import pty
import select
import signal
import sys
import time

split = sys.argv.index("--")
waits = [float(x) for x in sys.argv[1:split]]
command = sys.argv[split + 1:]

pid, master = pty.fork()
if pid == 0:
    # A job started in the background by a script has SIGINT ignored, and would pass that on.
    signal.signal(signal.SIGINT, signal.SIG_DFL)
    os.execvp(command[0], command)

start = time.monotonic()
due = []
total = 0.0
for wait in waits:
    total += wait
    due.append(start + total)

status = None
deadline = start + 120
while True:
    now = time.monotonic()
    if due and now >= due[0]:
        due.pop(0)
        sys.stdout.write("\n[pty_run: Ctrl+C]\n")
        sys.stdout.flush()
        os.write(master, b"\x03")
    if now > deadline:
        sys.stdout.write("\n[pty_run: gave up after 120 s]\n")
        os.kill(pid, 9)
    ready, _, _ = select.select([master], [], [], 0.1)
    if ready:
        try:
            data = os.read(master, 4096)
        except OSError:
            data = b""
        if data:
            sys.stdout.write(data.decode("utf-8", "replace").replace("\r\n", "\n"))
            sys.stdout.flush()
            continue
    done, raw = os.waitpid(pid, os.WNOHANG)
    if done:
        status = os.waitstatus_to_exitcode(raw)
        # Whatever is still in the terminal's buffer.
        while True:
            ready, _, _ = select.select([master], [], [], 0.2)
            if not ready:
                break
            try:
                data = os.read(master, 4096)
            except OSError:
                break
            if not data:
                break
            sys.stdout.write(data.decode("utf-8", "replace").replace("\r\n", "\n"))
        break

print(f"[pty_run: exit status {status}]")
