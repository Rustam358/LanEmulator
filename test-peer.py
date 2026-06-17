"""
Fake peer for local loopback test of Wintun Ring Buffer + UDP.
Run this in a SECOND terminal while Step 2 PoC is running.

What it does:
1. Listens on UDP port 51821 (peer side)
2. Sends a test IP packet to localhost:51820 (Wintun side)
3. Wintun reads it from UDP, injects into virtual adapter
4. Virtual adapter "sees" the packet, Wintun reads it back
5. Wintun sends it via UDP to 51821 (us!)
6. We receive it back — roundtrip complete!
"""
import socket
import struct
import time

PEER_PORT = 51821
TUN_PORT = 51820
TUN_HOST = "127.0.0.1"

def make_dummy_ip_packet(src_ip="10.13.37.2", dst_ip="10.13.37.1", payload=b"HELLO_FROM_PEER"):
    """Build a minimal IPv4 packet (just enough for Wintun to accept it)."""
    version_ihl = 0x45  # IPv4, 5 words header
    dscp_ecn = 0
    total_length = 20 + len(payload)
    identification = 0x1234
    flags_offset = 0
    ttl = 64
    protocol = 253  # Experimental — won't be processed by OS
    checksum = 0
    src = socket.inet_aton(src_ip)
    dst = socket.inet_aton(dst_ip)

    header = struct.pack('!BBHHHBBH4s4s',
        version_ihl, dscp_ecn, total_length,
        identification, flags_offset,
        ttl, protocol, checksum,
        src, dst)

    return header + payload


def main():
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    sock.bind(("0.0.0.0", PEER_PORT))
    sock.settimeout(3.0)
    print(f"[PEER] Listening on UDP :{PEER_PORT}")

    # Send a test packet into the Wintun tunnel
    packet = make_dummy_ip_packet(payload=b"HELLO_FROM_PEER")
    sock.sendto(packet, (TUN_HOST, TUN_PORT))
    print(f"[PEER] Sent {len(packet)} bytes -> {TUN_HOST}:{TUN_PORT} (IPv4: 10.13.37.2 -> 10.13.37.1)")

    # Wait for echo back (Wintun should forward it back to us)
    print("[PEER] Waiting for echo from Wintun...")
    try:
        data, addr = sock.recvfrom(65535)
        print(f"[PEER] Received {len(data)} bytes from {addr}")
        print(f"[PEER] Payload: {data[-16:]}")  # Show last bytes
        if b"HELLO_FROM_PEER" in data:
            print("[PEER] >>> ROUNDTRIP SUCCESS! Packet went through Wintun and back.")
        else:
            print("[PEER] Got data back but payload differs (may be Wintun-modified)")
    except socket.timeout:
        print("[PEER] No echo received (timeout). Check:")
        print("       1. Is wintun-poc.exe running?")
        print("       2. Are both ports correct?")
        print("       3. Try ping 10.13.37.1 from cmd first")

    sock.close()


if __name__ == "__main__":
    main()
