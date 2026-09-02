#!/usr/bin/env python3
"""WinDrop bridge — lends an AWDL radio to a Windows machine.

Runs on a Linux box with OWL up and a real awdl0. Does two deliberately dumb things:

  * relays mDNS datagrams on and off the link
  * pipes TCP bytes between a peer and the Windows host

It parses no property lists, holds no keys and terminates no TLS. Every protocol
decision stays in the Windows process. That is the point: this file could be replaced
by different hardware without touching a line of protocol code, and a compromised
bridge still cannot read a transfer.

    sudo ./windrop-bridge.py --interface awdl0

For testing without AWDL, point it at any interface that has an IPv6 link-local
address — the relay logic does not care which:

    ./windrop-bridge.py --interface eth0

Protocol, newline-delimited JSON on the control connection:

    -> {"op":"hello","reversePort":N}
    <- {"ok":true,"interface":"awdl0","address":"fe80::..."}
    -> {"op":"mdns","data":"<base64>"}
    <- {"event":"mdns","data":"<base64>"}

Data connections carry one JSON line then raw bytes:

    outbound  Windows dials the data port, sends {"connect":"fe80::...","port":8770}
    inbound   the bridge dials Windows' reverse port, sends {"peer":"fe80::..."}
"""

import argparse
import base64
import json
import logging
import socket
import struct
import sys
import threading

MDNS_GROUP = "ff02::fb"
MDNS_PORT = 5353
AIRDROP_PORT = 8770

log = logging.getLogger("bridge")


def link_local_address(interface):
    """The interface's IPv6 link-local address, or None.

    Read from /proc rather than shelling out to `ip`, so the daemon has no dependency
    beyond the standard library on a machine that may be a bare Pi image.
    """
    try:
        with open("/proc/net/if_inet6") as handle:
            for line in handle:
                parts = line.split()
                if len(parts) < 6 or parts[5] != interface:
                    continue
                # Scope 0x20 is link-local; anything else is not what AWDL uses.
                if parts[3] != "20":
                    continue
                raw = parts[0]
                return ":".join(raw[i:i + 4] for i in range(0, 32, 4))
    except FileNotFoundError:
        return None

    return None


class Bridge:
    def __init__(self, interface, control_port, data_port, airdrop_port):
        self.interface = interface
        self.control_port = control_port
        self.data_port = data_port
        self.airdrop_port = airdrop_port

        self.index = socket.if_nametoindex(interface)
        self.address = link_local_address(interface)

        if self.address is None:
            raise SystemExit(
                f"{interface} has no IPv6 link-local address.\n"
                f"If this is awdl0, OWL is probably not running."
            )

        self.control = None            # the connected Windows control socket
        self.control_lock = threading.Lock()
        self.reverse_port = None
        self.windows_host = None

    # ---- control ---------------------------------------------------------

    def send_control(self, message):
        with self.control_lock:
            if self.control is None:
                return
            try:
                self.control.sendall((json.dumps(message) + "\n").encode())
            except OSError:
                pass

    def serve_control(self):
        listener = socket.socket(socket.AF_INET6, socket.SOCK_STREAM)
        listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        listener.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 0)
        listener.bind(("", self.control_port))
        listener.listen(4)

        log.info("control listening on %d", self.control_port)

        while True:
            conn, peer = listener.accept()
            log.info("control connection from %s", peer[0])

            # One controller at a time: the bridge owns a single radio, and two
            # controllers advertising different services on it would collide.
            with self.control_lock:
                if self.control is not None:
                    try:
                        self.control.close()
                    except OSError:
                        pass
                self.control = conn
                self.windows_host = peer[0]

            threading.Thread(target=self.read_control, args=(conn,), daemon=True).start()

    def read_control(self, conn):
        buffer = b""

        try:
            while True:
                chunk = conn.recv(65536)
                if not chunk:
                    break

                buffer += chunk

                while b"\n" in buffer:
                    line, buffer = buffer.split(b"\n", 1)
                    if line.strip():
                        self.handle_control(json.loads(line.decode()))
        except (OSError, ValueError) as error:
            log.info("control connection ended: %s", error)
        finally:
            with self.control_lock:
                if self.control is conn:
                    self.control = None
            try:
                conn.close()
            except OSError:
                pass

    def handle_control(self, message):
        op = message.get("op")

        if op == "hello":
            self.reverse_port = int(message["reversePort"])
            log.info("controller will accept inbound on port %d", self.reverse_port)
            self.send_control({
                "ok": True,
                "interface": self.interface,
                "address": self.address,
            })

        elif op == "mdns":
            payload = base64.b64decode(message["data"])
            self.send_mdns(payload)

        else:
            log.warning("unknown op %r", op)

    # ---- mDNS relay ------------------------------------------------------

    def make_mdns_socket(self):
        sock = socket.socket(socket.AF_INET6, socket.SOCK_DGRAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        sock.bind(("", MDNS_PORT))

        group = socket.inet_pton(socket.AF_INET6, MDNS_GROUP) + struct.pack("I", self.index)
        sock.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_JOIN_GROUP, group)
        sock.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_MULTICAST_IF, struct.pack("I", self.index))
        return sock

    def send_mdns(self, payload):
        try:
            self.mdns.sendto(payload, (MDNS_GROUP, MDNS_PORT, 0, self.index))
        except OSError as error:
            log.warning("could not send mDNS: %s", error)

    def serve_mdns(self):
        self.mdns = self.make_mdns_socket()
        log.info("mDNS relay joined %s on %s", MDNS_GROUP, self.interface)

        while True:
            try:
                payload, _ = self.mdns.recvfrom(9000)
            except OSError as error:
                log.warning("mDNS receive failed: %s", error)
                continue

            self.send_control({
                "event": "mdns",
                "data": base64.b64encode(payload).decode(),
            })

    # ---- TCP relay -------------------------------------------------------

    @staticmethod
    def pipe(source, sink):
        try:
            while True:
                chunk = source.recv(65536)
                if not chunk:
                    break
                sink.sendall(chunk)
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

    def couple(self, a, b):
        threading.Thread(target=self.pipe, args=(a, b), daemon=True).start()
        threading.Thread(target=self.pipe, args=(b, a), daemon=True).start()

    def serve_data(self):
        """Outbound: Windows asks us to dial a peer on the AWDL link."""
        listener = socket.socket(socket.AF_INET6, socket.SOCK_STREAM)
        listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        listener.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 0)
        listener.bind(("", self.data_port))
        listener.listen(8)

        log.info("data listening on %d", self.data_port)

        while True:
            conn, _ = listener.accept()
            threading.Thread(target=self.handle_data, args=(conn,), daemon=True).start()

    def handle_data(self, conn):
        try:
            line = b""
            while not line.endswith(b"\n"):
                byte = conn.recv(1)
                if not byte:
                    conn.close()
                    return
                line += byte

            request = json.loads(line.decode())
            address = request["connect"]
            port = int(request.get("port", self.airdrop_port))

            # Pick the family from the address. AWDL peers are always IPv6 link-local,
            # but an IPv4 address is what shows up when the bridge is exercised over an
            # ordinary interface, and refusing it would make the thing untestable.
            if ":" in address:
                peer = socket.socket(socket.AF_INET6, socket.SOCK_STREAM)
                peer.settimeout(15)
                # The scope index is ours to supply: a link-local address means nothing
                # without the interface it lives on, and Windows cannot know ours.
                peer.connect((address, port, 0, self.index))
            else:
                peer = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
                peer.settimeout(15)
                peer.connect((address, port))

            peer.settimeout(None)

            conn.sendall(b'{"ok":true}\n')
            log.info("relaying to [%s]:%d", address, port)
            self.couple(conn, peer)

        except Exception as error:  # noqa: BLE001 - report anything back to the caller
            log.warning("outbound relay failed: %s", error)
            try:
                conn.sendall((json.dumps({"ok": False, "error": str(error)}) + "\n").encode())
                conn.close()
            except OSError:
                pass

    def serve_airdrop(self):
        """Inbound: a peer connects to us on the link; hand it to Windows."""
        listener = socket.socket(socket.AF_INET6, socket.SOCK_STREAM)
        listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        listener.bind((self.address, self.airdrop_port, 0, self.index))
        listener.listen(8)

        log.info("airdrop listening on [%s%%%s]:%d", self.address, self.interface, self.airdrop_port)

        while True:
            conn, peer = listener.accept()
            threading.Thread(target=self.handle_inbound, args=(conn, peer), daemon=True).start()

    def handle_inbound(self, conn, peer):
        if self.reverse_port is None or self.windows_host is None:
            log.warning("inbound from %s but no controller is connected", peer[0])
            conn.close()
            return

        try:
            windows = socket.create_connection((self.windows_host, self.reverse_port), timeout=15)
            windows.settimeout(None)
            windows.sendall((json.dumps({"peer": peer[0]}) + "\n").encode())

            log.info("inbound from %s handed to controller", peer[0])
            self.couple(conn, windows)

        except OSError as error:
            log.warning("could not hand inbound connection to controller: %s", error)
            conn.close()

    # ---- run -------------------------------------------------------------

    def run(self):
        log.info("bridge on %s (index %d), address %s", self.interface, self.index, self.address)

        for target in (self.serve_mdns, self.serve_data, self.serve_airdrop):
            threading.Thread(target=target, daemon=True).start()

        self.serve_control()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("-i", "--interface", default="awdl0",
                        help="interface to relay on (default: awdl0)")
    parser.add_argument("--control-port", type=int, default=7788)
    parser.add_argument("--data-port", type=int, default=7789)
    parser.add_argument("--airdrop-port", type=int, default=AIRDROP_PORT)
    parser.add_argument("-v", "--verbose", action="store_true")

    args = parser.parse_args()

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s %(levelname)-7s %(message)s",
    )

    bridge = Bridge(args.interface, args.control_port, args.data_port, args.airdrop_port)

    try:
        bridge.run()
    except KeyboardInterrupt:
        log.info("stopping")
        return 0

    return 0


if __name__ == "__main__":
    sys.exit(main())
