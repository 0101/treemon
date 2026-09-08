#!/usr/bin/env python3
"""Expose a loopback-only Treemon to Windows when WSL's own localhost forwarding is not working.

Treemon binds 127.0.0.1 deliberately - the CSRF guard depends on it, and the dashboard can spawn
terminals - so nothing outside the WSL VM can reach it directly. WSL normally bridges that itself,
but when its relay is absent the port is unreachable from Windows even though the VM's address is.

This listens on the VM's interface and forwards to that loopback port. The VM sits behind NAT, so
what becomes reachable is the Windows host, not the network the machine is on.

    python3 scripts/wsl-forward.py [listen_port] [target_port]

Then browse to http://<the address printed>:<listen_port> from Windows. Prefer WSL's mirrored
networking mode if you can restart WSL: it shares loopback both ways and needs no relay at all.
"""
import socket
import subprocess
import sys
import threading

LISTEN_PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 5010
TARGET = ("127.0.0.1", int(sys.argv[2]) if len(sys.argv) > 2 else 5000)
BUFFER = 65536


def pump(source, sink):
    try:
        while True:
            data = source.recv(BUFFER)
            if not data:
                break
            sink.sendall(data)
    except OSError:
        pass
    finally:
        for sock in (source, sink):
            try:
                sock.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            try:
                sock.close()
            except OSError:
                pass


def serve(client):
    try:
        upstream = socket.create_connection(TARGET, timeout=10)
    except OSError:
        client.close()
        return

    for a, b in ((client, upstream), (upstream, client)):
        threading.Thread(target=pump, args=(a, b), daemon=True).start()


def vm_address():
    try:
        out = subprocess.run(["hostname", "-I"], capture_output=True, text=True, timeout=5).stdout
        return out.split()[0] if out.split() else "<the VM address>"
    except Exception:
        return "<the VM address>"


listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
listener.bind(("0.0.0.0", LISTEN_PORT))
listener.listen(64)

print(f"forwarding {vm_address()}:{LISTEN_PORT} -> {TARGET[0]}:{TARGET[1]}", flush=True)
print(f"open http://{vm_address()}:{LISTEN_PORT} from Windows", flush=True)

while True:
    connection, _ = listener.accept()
    serve(connection)
