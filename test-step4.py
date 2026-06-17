"""Test Step 4: Virtual DHCP + Routing"""
import urllib.request, json, threading, time
BASE = 'http://localhost:8000'

def post(path, data):
    req = urllib.request.Request(f'{BASE}{path}', data=json.dumps(data).encode(),
                                  headers={'Content-Type': 'application/json'}, method='POST')
    return json.loads(urllib.request.urlopen(req).read())

def get(path):
    return json.loads(urllib.request.urlopen(f'{BASE}{path}').read())

# Step 1: Player 1 registers
r1 = post('/register', {'player_id': 'TEST_PC', 'room_id': 'vpn444', 'udp_port': 51820})
print(f'P1: {r1}')
assert r1['virtual_ip'] == '10.13.37.1', f'Expected 10.13.37.1, got {r1["virtual_ip"]}'

# Step 2: Player 2 registers
r2 = post('/register', {'player_id': 'FRIEND_PC', 'room_id': 'vpn444', 'udp_port': 51820})
print(f'P2: {r2}')
assert r2['virtual_ip'] == '10.13.37.2', f'Expected 10.13.37.2, got {r2["virtual_ip"]}'

# Step 3: Poll — should have virtual_ips
poll = get('/poll?room_id=vpn444')
print(f'Poll: {poll}')
assert poll['status'] == 'ready'
assert poll['player_count'] == 2
for p in poll['players']:
    print(f'  {p["player_id"]}: public={p["ip"]}:{p["udp_port"]} vpn={p["virtual_ip"]}')
    assert 'virtual_ip' in p

# Step 4: Player 1 re-registers (should return same virtual IP)
r1b = post('/register', {'player_id': 'TEST_PC', 'room_id': 'vpn444', 'udp_port': 51820})
print(f'P1 re-register: {r1b}')
assert r1b['virtual_ip'] == '10.13.37.1', 'Re-register should keep same IP'

# Step 5: Rooms debug
rooms = get('/rooms')
print(f'Rooms: {rooms}')

print('\nALL TESTS PASSED - Virtual DHCP working!')
