"""Validate offline photo textures, UVs, and retained image detail in a real generated GLB.
Usage: python tests/reconstruction-texture.py MODEL.glb [original-photo-directory]
Requires numpy and opencv-python; uses only public synthetic fixtures.
"""
from pathlib import Path
import sys, json, struct
import cv2
import numpy as np

model=Path(sys.argv[1])
source=Path(sys.argv[2]) if len(sys.argv)>2 else Path(".test-data/reconstruction/synthetic48")
data=model.read_bytes();assert data[:4]==b"glTF" and struct.unpack_from("<I",data,8)[0]==len(data)
n=struct.unpack_from("<I",data,12)[0];meta=json.loads(data[20:20+n]);binary=data[28+n:]
assert len(binary)==meta["buffers"][0]["byteLength"]
checks=[]
def check(ok,label):
    assert ok,label
    checks.append(label);print("PASS",label,flush=True)
def values(index,components):
    a=meta["accessors"][index];view=meta["bufferViews"][a["bufferView"]]
    dtype="<f4" if a["componentType"]==5126 else "<u4"
    return np.ndarray((a["count"],components),dtype=dtype,buffer=binary,offset=view.get("byteOffset",0)+a.get("byteOffset",0),strides=(view.get("byteStride",components*4),4))
originals=[]
for path in sorted(source.glob("*.jpg")):
    image=cv2.imread(str(path));originals.append(cv2.resize(image,(96,96),interpolation=cv2.INTER_AREA).astype(float))
check(bool(meta.get("images")),"Model embeds real image textures")
worst=0.;pixels=0
for image in meta["images"]:
    check_uri="uri" not in image and image.get("mimeType")=="image/jpeg"
    assert check_uri
    view=meta["bufferViews"][image["bufferView"]];offset=view.get("byteOffset",0);length=view["byteLength"]
    assert offset%4==0 and offset+length<=len(binary)
    decoded=cv2.imdecode(np.frombuffer(binary[offset:offset+length],np.uint8),cv2.IMREAD_COLOR);assert decoded is not None
    assert max(decoded.shape[:2])<=meta["extras"]["textureMaxSide"]
    pixels+=decoded.shape[0]*decoded.shape[1]
    thumb=cv2.resize(decoded,(96,96),interpolation=cv2.INTER_AREA).astype(float)
    error=min(np.abs(thumb-original).mean() for original in originals);worst=max(worst,error)
check(worst<6,"Embedded texture pixels retain original-photo detail without vertex-color averaging")
check(pixels<=(128 if meta["extras"]["textureMaxSide"]>2048 else 64)*1024*1024,"Decoded texture pixels stay inside the mobile memory budget")
triangles=0;textured=0
for primitive in meta["meshes"][0]["primitives"]:
    positions=values(primitive["attributes"]["POSITION"],3);indices=values(primitive["indices"],1)
    assert np.isfinite(positions).all() and int(indices.max())<len(positions) and len(indices)%3==0
    triangles+=len(indices)//3
    if "TEXCOORD_0" in primitive["attributes"]:
        uv=values(primitive["attributes"]["TEXCOORD_0"],2)
        assert len(uv)==len(positions) and np.isfinite(uv).all() and uv.min()>=0 and uv.max()<=1
        assert "COLOR_0" not in primitive["attributes"]
        textured+=len(indices)//3
check(textured>triangles*.8,"Most reconstructed triangles project visible source photographs with bounded UVs")
report={"success":True,"checks":checks,"textures":len(meta["images"]),"decodedTexturePixels":pixels,"worstMeanAbsolutePixelError":worst,"triangles":triangles,"texturedTriangles":textured}
model.with_suffix(".texture-verification.json").write_text(json.dumps(report,indent=2),encoding="utf-8")
