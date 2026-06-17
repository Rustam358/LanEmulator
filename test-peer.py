"""
ICMP-based loopback test for Wintun Ring Buffer + UDP.
Run in a SECOND terminal while Step 2 PoC is running.
"""
import socket
import struct
import time

PEER_PORT = 51821
TUN_PORT = 51820
TUN_HOST = "127.0.0.1"

def checksum(data):
    """16-bit one's complement checksum."""
    s = sum((int.from_bytes(data[i:i+2],'big') for i in range(0,len(data),2)))
    s = (s >> 16) + (s & 0xFFFF)
    s += s >> 16
    return (~s) & 0xFFFF

def make_icmp_echo(src_ip, dst_ip, seq=1):
    """Build IPv4 + ICMP Echo Request packet."""
    # ICMP header
    icmp_type = 8  # Echo Request
    icmp_code = 0
    icmp_id = 0x1337
    icmp_seq = seq
    icmp_payload = b"WINTCKTEST"

    icmp_header = struct.pack('!BBHHH', icmp_type, icmp_code, 0, icmp_id, icmp_seq) + icmp_payload
    icmp_csum = checksum(icmp_header)
    icmp_header = struct.pack('!BBHHH', icmp_type, icmp_code, icmp_csum, icmp_id, icmp_seq) + icmp_payload

    # IPv4 header
    version_ihl = 0x45
    total_length = 20 + len(icmp_header)
    ip_header = struct.pack('!BBHHHBBH4s4s',
        0x45, 0, total_length,
        0x5678, 0,
        128, 1, 0,  # TTL=128, protocol=ICMP
        socket.inet_aton(src_ip), socket.inet_aton(dst_ip))
    ip_csum = checksum(ip_header)
    ip_header = struct.pack('!BBHHHBBH4s4s',
        0x45, 0, total_length,
        0x5678, 0,
        128, 1, ip_csum,
        socket.inet_aton(src_ip), socket.inet_aton(dst_ip))

    return ip_header + icmp_header


def main():
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    sock.bind(("0.0.0.0", PEER_PORT))
    sock.settimeout(3.0)
    print(f"[PEER] Listening on UDP :{PEER_PORT}")

    packet = make_icmp_echo("10.13.37.99", "10.13.37.1", seq=1)
    sock.sendto(packet, (TUN_HOST, TUN_PORT))
    print(f"[PEER] Sent ICMP Echo ({len(packet)} bytes) 10.13.37.99 -> 10.13.37.1")

    try:
        data, addr = sock.recvfrom(65535)
        print(f"[PEER] Received {len(data)} bytes from {addr}")
        # Check for ICMP Echo Reply (type 0) or any response
        if len(data) > 20:
            ip_data = data[20:]  # Skip IP header if present
            if len(ip_data) >= 4:
                icmp_type = ip_data[0]
                print(f"[PEER] ICMP type={icmp_type} " +
                      ("ECHO REPLY!" if icmp_type == 0 else "(other)"))
                if icmp_type == 0:
                    print("[PEER] >>> ROUNDTRIP SUCCESS! ICMP packet went through Wintun Ring Buffer and back.")
                    return
        print(f"[PEER] Raw data (hex): {data[:50].hex()}")
    except socket.timeout:
        print("[PEER] No echo received (timeout).")
        print("       The UDP path works (ping 10.13.37.1 works),")
        print("       but ICMP reply may be filtered by Windows firewall.")

    sock.close()


if __name__ == "__main__":
    main()
