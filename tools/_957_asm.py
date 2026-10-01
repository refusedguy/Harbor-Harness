#!/usr/bin/env python3
"""#957 — read a .NET assembly's public TypeDef list without dotnet.

The XML doc file is NOT a complete surface: Microsoft.Extensions.Http
references IHttpClientFactory 64 times and declares it zero times
(`T:` entries: 17, and it is not among them). A guard built on the XML
alone therefore cannot see the one type `tests/Harbor.App.Cli.Tests`
actually depends on. This reads the TypeDef table straight out of the
metadata, per ECMA-335 II.22.
"""
import struct, sys


def _tables(blob):
    pe = struct.unpack_from("<I", blob, 0x3C)[0]
    assert blob[pe:pe + 4] == b"PE\0\0", "not a PE image"
    nsec = struct.unpack_from("<H", blob, pe + 6)[0]
    optsz = struct.unpack_from("<H", blob, pe + 20)[0]
    opt = pe + 24
    magic = struct.unpack_from("<H", blob, opt)[0]
    dd = opt + (112 if magic == 0x20B else 96)
    clr_rva, _ = struct.unpack_from("<II", blob, dd + 14 * 8)
    secs, off = [], opt + optsz
    for i in range(nsec):
        _, vs, va, rs, ptr = struct.unpack_from("<8sIIII", blob, off + 40 * i)
        secs.append((va, vs, ptr, rs))

    def r2o(rva):
        for va, vs, ptr, rs in secs:
            if va <= rva < va + max(vs, rs):
                return ptr + (rva - va)
        return None

    o = r2o(clr_rva)
    md_rva, _ = struct.unpack_from("<II", blob, o + 8)
    mo = r2o(md_rva)
    assert blob[mo:mo + 4] == b"BSJB", "no metadata root"

    vlen = struct.unpack_from("<I", blob, mo + 12)[0]
    p = mo + 16 + vlen + 2
    n = struct.unpack_from("<H", blob, p)[0]
    p += 2
    streams = {}
    for _ in range(n):
        so, sz = struct.unpack_from("<II", blob, p)
        p += 8
        nm = b""
        while blob[p]:
            nm += bytes([blob[p]])
            p += 1
        p += 1
        p = (p + 3) & ~3
        streams[nm.decode()] = (mo + so, sz)
    return blob, streams


def public_types(path):
    """Fully-qualified names of every TypeDef, nested names joined with '+'."""
    blob, streams = _tables(open(path, "rb").read())
    to, tsz = streams["#Strings"]
    tilde, tszs = streams["#~"]

    heapsizes = blob[tilde + 6]
    str_wide = bool(heapsizes & 1)
    guid_wide = bool(heapsizes & 2)
    blob_wide = bool(heapsizes & 4)
    valid = struct.unpack_from("<Q", blob, tilde + 8)[0]
    p = tilde + 24

    def idx_size(n):
        return 4 if n >= 0x10000 else 2

    rows = {}
    for t in range(64):
        if valid >> t & 1:
            rows[t] = struct.unpack_from("<I", blob, p)[0]
            p += 4
    p = (p + 3) & ~3

    strsz, guidsz, blobsz = 4 if str_wide else 2, 4 if guid_wide else 2, 4 if blob_wide else 2

    # Coded index width: 4 bytes when the largest referenced table's row count
    # no longer fits the tag bits (ECMA-335 II.24.2.6).
    def coded(tag_bits, tables):
        mx = max((rows.get(t, 0) for t in tables), default=0)
        return 4 if mx >= (1 << (16 - tag_bits)) else 2

    typedef_ext = coded(2, (0x02, 0x01, 0x1B))          # TypeDefOrRef

    # Row sizes for the tables we must skip to reach TypeDef (0x02).
    # Module (0x00): Generation(2) + Name + Mvid + EncId + EncBaseId — three
    # GUID heaps, not one. Getting this wrong shifts every later read.
    S = 2 + strsz + guidsz * 3
    TYPEREF = coded(3, (0x01, 0x02, 0x1B)) + strsz * 2  # ResolutionScope+Name+Namespace

    tables_start = p
    off = tables_start + rows.get(0x00, 0) * S + rows.get(0x01, 0) * TYPEREF
    row = 4 + strsz * 2 + typedef_ext \
          + idx_size(rows.get(0x04, 0)) + idx_size(rows.get(0x06, 0))

    def s(o):
        e = blob.index(b"\0", to + o)
        return blob[to + o:e].decode("utf-8", "replace")

    out = []
    nested = {}
    for i in range(rows.get(0x02, 0)):
        base_off = off + i * row
        name_i = struct.unpack_from("<H" if not str_wide else "<I", blob, base_off + 4)[0]
        ns_i = struct.unpack_from("<H" if not str_wide else "<I", blob, base_off + 4 + strsz)[0]
        ext_i = struct.unpack_from("<H" if typedef_ext == 2 else "<I", blob,
                                   base_off + 4 + strsz * 2)[0]
        name, ns = s(name_i), s(ns_i)
        if name.startswith("<") and ns == "":
            continue          # <Module> and compiler-generated display classes
        full = f"{ns}.{name}" if ns else name
        out.append(full)
    return out


if __name__ == "__main__":
    for p in sys.argv[1:]:
        t = public_types(p)
        print(f"### {p} -> {len(t)} TypeDefs")
        for x in sorted(t):
            print("   ", x)