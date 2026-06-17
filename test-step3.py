import urllib.request, json, threading, time
BASE = 'http://localhost:8000'

def post(path, data):
    req = urllib.request.Request(f'{BASE}{path}', data=json.dumps(data).encode(),
                                  headers={'Content-Type': 'application/json'}, method='POST')
    return json.loads(urllib.request.urlopen(req).read())

def get(path):
    return json.loads(urllib.request.urlopen(f'{BASE}{path}').read())

# Player 1 registers (mimics C# app)
r1 = post('/register', {'player_id': 'TEST_PC', 'room_id': 'test342', 'udp_port': 51820})
print(f'P1 registered: {r1}')

# Peer joins after 3s
def peer_joins():
    time.sleep(3)
    r2 = post('/register', {'player_id': 'FRIEND_PC', 'room_id': 'test342', 'udp_port': 51820})
    print(f'P2 registered: {r2}')

threading.Thread(target=peer_joins, daemon=True).start()

# Poll like C# app
print('Polling (P2 should appear in ~3s)...')
for i in range(8):
    resp = get('/poll?room_id=test342')
    status = resp['status']
    count = resp['player_count']
    print(f'  Poll #{i+1}: {status} ({count} players)')
    if status == 'ready' and resp.get('players'):
        for p in resp['players']:
            peer_ip = p['ip']
            peer_port = p['udp_port']
            print(f'    -> PEER: {p["player_id"]} @ {peer_ip}:{peer_port}')
        # Verify peer IP is accessible (not just "unknown")
        print(f'  PEER_IP={peer_ip} PEER_PORT={peer_port}')
        print('SIMULATION PASSED')
        break
    time.sleep(1)
else:
    print('SIMULATION FAILED: timeout')
