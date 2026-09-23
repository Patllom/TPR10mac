"""Exercise the real CLI through a PTY; test credentials enter via stdin, never argv."""
import errno
import json
import os
import pty
import select
import subprocess
import sys
import termios
import time

data = json.load(sys.stdin)
master, slave = pty.openpty()
child = subprocess.Popen([sys.argv[1], sys.argv[2], "--bootstrap-admin"], stdin=slave,
                         stdout=slave, stderr=slave, close_fds=True)
output = bytearray()
sent_name = sent_password = False
deadline = time.monotonic() + 15
try:
    while time.monotonic() < deadline:
        ready, _, _ = select.select([master], [], [], 0.02)
        if ready:
            try:
                chunk = os.read(master, 65536)
            except OSError as error:
                if error.errno == errno.EIO:
                    break
                raise
            if not chunk:
                break
            output.extend(chunk)
        text = output.decode("utf-8", errors="replace")
        if not sent_name and "ชื่อผู้ใช้:" in text:
            os.write(master, (data["username"] + "\n").encode())
            sent_name = True
        if not sent_password and "รหัสผ่าน:" in text and not (termios.tcgetattr(slave)[3] & termios.ECHO):
            os.write(master, (data["password"] + "\r").encode())
            sent_password = True
        if child.poll() is not None and not ready:
            break
    if child.poll() is None:
        child.kill()
        child.wait()
        sys.stdout.write(output.decode("utf-8", errors="replace"))
        sys.stderr.write("CLI did not complete within the test deadline\n")
        sys.exit(124)
    sys.stdout.write(output.decode("utf-8", errors="replace"))
    sys.exit(child.returncode)
finally:
    if child.poll() is None:
        child.kill()
        child.wait()
    os.close(master)
    os.close(slave)
