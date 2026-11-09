"""Report non-system native imports that must accompany an unpackaged WinUI build."""
import json
import os
from pathlib import Path
import struct
import sys

root = Path(sys.argv[1]).resolve()
system = Path(os.environ["SystemRoot"]) / "System32"
available = {p.name.lower() for p in root.glob("*") if p.is_file()}
missing = {}
for file in root.glob("*.dll"):
    data = file.read_bytes()
    try:
        pe = struct.unpack_from("<I", data, 0x3C)[0]
        if data[pe:pe+4] != b"PE\0\0": continue
        count = struct.unpack_from("<H", data, pe+6)[0]
        optional_size = struct.unpack_from("<H", data, pe+20)[0]
        optional = pe+24
        directory = optional + (112 if struct.unpack_from("<H", data, optional)[0] == 0x20B else 96)
        imports = struct.unpack_from("<I", data, directory+8)[0]
        sections = []
        for i in range(count):
            offset = optional+optional_size+i*40
            size, virtual, raw_size, raw = struct.unpack_from("<IIII", data, offset+8)
            sections.append((virtual, max(size, raw_size), raw))
        def offset(rva):
            for virtual, size, raw in sections:
                if virtual <= rva < virtual+size: return raw+rva-virtual
            raise ValueError("unmapped RVA")
        if imports == 0: continue
        entry = offset(imports)
        while entry+20 <= len(data):
            row = struct.unpack_from("<IIIII", data, entry)
            if not any(row): break
            start = offset(row[3]); name = data[start:data.index(b"\0", start)].decode("ascii").lower()
            redist = name.startswith(("msvcp140", "vcruntime140", "concrt140"))
            if not name.startswith(("api-ms-", "ext-ms-")) and name not in available and (redist or not (system/name).exists()): missing.setdefault(file.name, []).append(name)
            entry += 20
    except (ValueError, struct.error, IndexError):
        continue
print(json.dumps({"directory": str(root), "missingImports": missing}, ensure_ascii=False, indent=2))
sys.exit(bool(missing))
