import urllib.request, json, os, zipfile, shutil

# Search GitHub for Goldberg Emulator releases
q = "goldberg emulator steam_api64.dll"
url = f"https://api.github.com/search/code?q={q.replace(' ', '+')}&per_page=5"
req = urllib.request.Request(url, headers={"Accept": "application/vnd.github.v3+json", "User-Agent": "python"})
try:
    data = json.loads(urllib.request.urlopen(req).read())
    for item in data.get("items", [])[:5]:
        print(f"  {item['repository']['full_name']}: {item['path']}")
except Exception as e:
    print(f"Search error: {e}")

# Also try direct download of known mirror
# The community fork "goldberg_emulator" often has CI-built artifacts
print("\nTrying to find prebuilt DLL from community sources...")

# Check if there are any .dll files in the cloned source
dll_dir = r"D:\Programs\AutoClawLinda\goldberg_emulator"
for root, dirs, files in os.walk(dll_dir):
    for f in files:
        if f.endswith('.dll'):
            print(f"Found DLL: {os.path.join(root, f)} ({os.path.getsize(os.path.join(root, f))} bytes)")
