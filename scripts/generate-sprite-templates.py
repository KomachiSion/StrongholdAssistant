"""生成小人比对包；需要 numpy、opencv-python，输入为已渲染的默认皮肤 PNG。

用法：python scripts/generate-sprite-templates.py --rendered out/134/rendered
包为 gzip：SPRT0001、记录数(uint32)，随后为姓名长度(uint16)/UTF-8、
阶数/宽/高(3个uint16)、BGR像素、单通道蒙版。整数均为小端。
"""
import argparse
import gzip
import json
import struct
from pathlib import Path

import cv2
import numpy as np


def generate(rendered, operators, output):
    records = []
    covered = set()
    for op in json.loads(operators.read_text(encoding="utf-8-sig"))["operators"]:
        name = op["name"].strip("“”")
        seen = set()
        # 只取默认皮肤；少数干员只有一个朝向，以实际渲染资源为准。
        paths = sorted(rendered.glob(f"defaultskin-{name}-*.png"))
        if not paths:
            raise ValueError(f"缺少默认模板：{name}")
        for path in paths:
            image = cv2.imdecode(np.fromfile(path, dtype=np.uint8), cv2.IMREAD_UNCHANGED)
            mask = (image[:, :, 3] > 100).astype(np.uint8) * 255
            x, y, w, h = cv2.boundingRect(mask)
            scale = 60 / max(w, h)
            ww, hh = max(1, round(w * scale)), max(1, round(h * scale))
            a = np.zeros((64, 64, 3), np.uint8)
            b = np.zeros((64, 64), np.uint8)
            xx, yy = (64 - ww) // 2, (64 - hh) // 2
            a[yy:yy+hh, xx:xx+ww] = cv2.resize(image[y:y+h, x:x+w, :3], (ww, hh))
            b[yy:yy+hh, xx:xx+ww] = cv2.resize(mask[y:y+h, x:x+w], (ww, hh), interpolation=cv2.INTER_NEAREST)
            # 保存60像素归一结果；尺度与镜像在首次使用某人时缓存，避免包重复存十份。
            x, y, w, h = cv2.boundingRect(b)
            payload = struct.pack("<3H", op["tier"], w, h) + a[y:y+h, x:x+w].tobytes() + b[y:y+h, x:x+w].tobytes()
            if payload not in seen:
                seen.add(payload)
                encoded = name.encode("utf-8")
                records.append(struct.pack("<H", len(encoded)) + encoded + payload)
            covered.add(name)
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("wb") as raw, gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0, compresslevel=9) as dest:
        dest.write(b"SPRT0001" + struct.pack("<I", len(records)))
        for record in records:
            dest.write(record)
    print(json.dumps(dict(operators=len(covered), templates=len(records), bytes=output.stat().st_size)))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rendered", type=Path, required=True)
    parser.add_argument("--operators", type=Path, default=Path("data/operators.json"))
    parser.add_argument("--output", type=Path, default=Path("data/sprite-templates.bin.gz"))
    args = parser.parse_args()
    generate(args.rendered, args.operators, args.output)
