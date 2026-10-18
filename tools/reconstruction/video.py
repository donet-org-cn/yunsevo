"""Dense, bounded server-side extraction from the original video, without upscaling."""

from pathlib import Path
import json
import math
import os
import subprocess


def extract(source: Path, output: Path, ffmpeg: Path, ffprobe: Path, report):
    if source.stat().st_size > 512 * 1024**2:
        raise ValueError("视频超过 512 MB。")
    flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
    result = subprocess.run(
        [
            str(ffprobe),
            "-v",
            "error",
            "-protocol_whitelist",
            "file,pipe",
            "-select_streams",
            "v:0",
            "-show_entries",
            "stream=width,height:format=duration",
            "-of",
            "json",
            str(source),
        ],
        capture_output=True,
        check=True,
        timeout=30,
        creationflags=flags,
    )
    metadata = json.loads(result.stdout)
    duration = float(metadata["format"]["duration"])
    stream = metadata["streams"][0]
    if (
        not 3 <= duration <= 180
        or not 1 <= stream["width"] <= 16000
        or not 1 <= stream["height"] <= 16000
    ):
        raise ValueError("请选择 3–180 秒、尺寸有效的连续视频。")
    count = min(120, max(12, math.ceil(duration * 1.75)))
    output.mkdir(parents=True, exist_ok=True)
    report(2, f"从原视频提取约 {count} 个连续视角，不放大原画面")
    filter_text = f"fps={count / duration:.10f},scale=w='min(iw,3840)':h='min(ih,3840)':force_original_aspect_ratio=decrease"
    with (output.parent / "video-extraction.log").open("wb") as log:
        subprocess.run(
            [
                str(ffmpeg),
                "-hide_banner",
                "-loglevel",
                "error",
                "-nostdin",
                "-protocol_whitelist",
                "file,pipe",
                "-i",
                str(source),
                "-an",
                "-vf",
                filter_text,
                "-frames:v",
                str(count),
                "-q:v",
                "2",
                str(output / "frame-%03d.jpg"),
            ],
            stdout=log,
            stderr=log,
            check=True,
            timeout=180,
            creationflags=flags,
        )
    # Add minimal provenance without re-encoding JPEG pixels.
    import piexif

    paths = sorted(output.glob("*.jpg"))
    if len(paths) < 6:
        raise ValueError("视频未提供足够有效画面。")
    for index, path in enumerate(paths):
        description = f"Video keyframe; source={source.name}; timestampUs={int((index + .5) * duration / count * 1e6)}"
        piexif.insert(
            piexif.dump({"0th": {270: description.encode("ascii", errors="replace"), 274: 1}}),
            str(path),
        )
    return {
        "sourceVideoSeconds": duration,
        "extractedFrames": len(paths),
        "sourceVideoWidth": stream["width"],
        "sourceVideoHeight": stream["height"],
    }
