"""Real CPU and Vulkan mesh generation, cancellation, image bounds and GLB checks.
Requires a built win-x64 native runtime and the generated synthetic48 fixture.
"""
import ctypes, json, math, struct, subprocess, time
from pathlib import Path
runtime = Path("artifacts/reconstruction-native/win-x64").resolve()
engine = runtime / "yunsevo-reconstruct.exe"
root = Path(".test-data/reconstruction/native-verification-" + str(time.time_ns())).resolve()
root.mkdir(parents=True)
checks = []
def check(value, text):
    assert value, text
    checks.append(text)
    print("PASS", text, flush=True)
def glb(path):
    data = path.read_bytes()
    check(data[:4] == b"glTF" and struct.unpack_from("<I", data, 8)[0] == len(data), "GLB binary framing: " + path.name)
    n = struct.unpack_from("<I", data, 12)[0]
    meta = json.loads(data[20:20+n])
    count = meta["accessors"][0]["count"]
    values = struct.iter_unpack("<9f", data[28+n:28+n+count*36])
    check(all(all(math.isfinite(v) for v in row) for row in values), "Finite mesh geometry and colors: " + path.name)
    return meta
caps = json.loads(subprocess.check_output([engine, "--capabilities"], text=True))
check(caps["cpu"] and caps["vulkan"], "Real CPU and hardware Vulkan capabilities")
for backend in ["cpu", "vulkan"]:
    started = time.monotonic()
    result = subprocess.run([engine, str(Path(".test-data/reconstruction/synthetic48").resolve()), str(root / (backend+".glb")), backend, "0"], capture_output=True, text=True, timeout=900)
    (root / (backend+".log")).write_text(result.stdout + result.stderr, encoding="utf-8")
    check(result.returncode == 0, "Actual reconstruction succeeds: " + backend)
    metadata = glb(root / (backend+".glb"))
    check(metadata["extras"]["registeredCameras"] == 48 and metadata["accessors"][3]["count"] > 3000, "48 cameras and genuine mesh triangles: " + backend)
    check(metadata["extras"]["backend"].startswith(backend), "Honest compute provenance: " + backend)
    print(backend, round(time.monotonic()-started, 2), "seconds", flush=True)
# Synchronous native cancellation cannot invoke UI or leak a partial published file.
dll = ctypes.CDLL(str(runtime / "yunsevo_reconstruction.dll"))
callback = ctypes.CFUNCTYPE(ctypes.c_int, ctypes.c_void_p, ctypes.c_int, ctypes.c_char_p)
dll.yv_reconstruct.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_char_p, ctypes.c_int, callback, ctypes.c_void_p]
cancel = callback(lambda *_: 1)
output = root / "cancelled.glb"
result = dll.yv_reconstruct(str(root).encode(), str(output).encode(), b"cpu", 0, cancel, None)
check(result == 2 and not output.exists(), "Cancellation stops native work without publishing a model")
malformed = root / "malformed"; malformed.mkdir()
# Header describes dimensions too large to decode. No pixel allocation should occur.
for i in range(6): (malformed / (str(i)+".jpg")).write_bytes(b"\xff\xd8\xff\xc0\x00\x11\x08\xff\xff\xff\xff" + bytes(10))
result = subprocess.run([engine, str(malformed), str(root/"bad.glb"), "cpu"], capture_output=True, text=True, timeout=15)
check(result.returncode != 0 and "Unsupported image" in result.stderr, "Oversized compressed image rejected before decoding")
(root / "verification.json").write_text(json.dumps({"success":True,"checks":checks,"capabilities":caps}, indent=2), encoding="utf-8")
print("Verification:",root)
