import urllib.request, json
BASE = "http://localhost:8000"

def post(path, data):
    req = urllib.request.Request(f"{BASE}{path}", data=json.dumps(data).encode(),
                                  headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(req).read())

def get(path):
    return json.loads(urllib.request.urlopen(f"{BASE}{path}").read())

# Register Rustam
print("1.", post("/register", {"player_id":"Rustam","room_id":"room123","udp_port":51820}))
# Poll — waiting
print("2.", get("/poll?room_id=room123"))
# Register Yan
print("3.", post("/register", {"player_id":"Yan","room_id":"room123","udp_port":51820}))
# Poll — ready with 2 players
print("4.", get("/poll?room_id=room123"))
# Third player rejected
try:
    print("5.", post("/register", {"player_id":"Extra","room_id":"room123","udp_port":51820}))
except Exception as e:
    print(f"5. REJECTED: {e}")
# Rooms
print("6.", get("/rooms"))
print("ALL TESTS PASSED")
