import urllib.request, json

# Try Steam community search (still works as of 2024+)
url = "https://steamcommunity.com/actions/SearchApps/Subnautica"
try:
    req = urllib.request.Request(url, headers={"User-Agent": "LanEmulator/1.0"})
    data = json.loads(urllib.request.urlopen(req, timeout=8).read())
    print(f"Community search: {json.dumps(data[:3], indent=2)}")
    if data:
        aid = data[0]["appid"]
        name = data[0]["name"]
        print(f"OK: {name} -> AppID {aid}")
except Exception as e:
    print(f"Error: {e}")
