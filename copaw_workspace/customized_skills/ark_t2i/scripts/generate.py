#!/usr/bin/env python3
"""Ark Text-to-Image generator (Volcengine Ark v3).

Reads configuration from env:
  - ARK_API_KEY (required)
  - ARK_BASE_URL (required) e.g. https://ark.cn-beijing.volces.com/api/v3
  - ARK_T2I_MODEL (required) e.g. doubao-seedream-4-5-251128
  - ARK_T2I_OUT_DIR (optional) default ./images
  - ARK_T2I_SIZE (optional) default 2K

Usage:
  python generate.py --prompt "..." [--size 2K] [--out-dir /path] [--model xxx] [--base-url xxx]

Outputs JSON to stdout:
  {"status":"success","file":"/path/to.png","url":"..."}
"""

from __future__ import annotations

import argparse
import base64
import json
import os
import re
import sys
import time
from pathlib import Path
from typing import Any, Optional

import requests


def _env(key: str, default: Optional[str] = None) -> Optional[str]:
    v = os.getenv(key)
    if v is None or v == "":
        return default
    return v


def _sanitize_filename(s: str, max_len: int = 80) -> str:
    s = s.strip().lower()
    s = re.sub(r"\s+", "_", s)
    s = re.sub(r"[^a-z0-9_\-]+", "", s)
    if not s:
        s = "image"
    return s[:max_len]


def _post_json(url: str, headers: dict[str, str], payload: dict[str, Any], timeout: int = 120) -> dict[str, Any]:
    resp = requests.post(url, headers=headers, json=payload, timeout=timeout)
    # Try to parse JSON even on errors
    try:
        data = resp.json()
    except Exception:
        data = {"raw": resp.text}

    if resp.status_code >= 400:
        raise RuntimeError(f"HTTP {resp.status_code}: {data}")
    return data


def _download(url: str, out_path: Path, timeout: int = 120) -> None:
    with requests.get(url, stream=True, timeout=timeout) as r:
        r.raise_for_status()
        out_path.parent.mkdir(parents=True, exist_ok=True)
        with out_path.open("wb") as f:
            for chunk in r.iter_content(chunk_size=1024 * 256):
                if chunk:
                    f.write(chunk)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--prompt", required=True)
    ap.add_argument("--size", default=_env("ARK_T2I_SIZE", "2K"))
    ap.add_argument("--model", default=_env("ARK_T2I_MODEL"))
    ap.add_argument("--base-url", default=_env("ARK_BASE_URL"))
    ap.add_argument("--out-dir", default=_env("ARK_T2I_OUT_DIR", "./images"))
    ap.add_argument("--watermark", action="store_true", default=False)
    ap.add_argument("--response-format", choices=["url", "b64_json"], default="url")
    args = ap.parse_args()

    api_key = _env("ARK_API_KEY")
    if not api_key:
        print(json.dumps({"status": "error", "error": "missing_env", "message": "ARK_API_KEY is required"}, ensure_ascii=False))
        return 2

    if not args.base_url:
        print(json.dumps({"status": "error", "error": "missing_env", "message": "ARK_BASE_URL is required"}, ensure_ascii=False))
        return 2

    if not args.model:
        print(json.dumps({"status": "error", "error": "missing_env", "message": "ARK_T2I_MODEL is required"}, ensure_ascii=False))
        return 2

    base_url = args.base_url.rstrip("/")
    # According to Volcengine Ark docs: POST /images/generations
    endpoint = f"{base_url}/images/generations"

    headers = {
        "Content-Type": "application/json",
        "Authorization": f"Bearer {api_key}",
    }

    payload: dict[str, Any] = {
        "model": args.model,
        "prompt": args.prompt,
        "size": args.size,
        "response_format": args.response_format,
        # Ark supports extra_body.watermark in OpenAI-compatible examples
        "extra_body": {"watermark": bool(args.watermark)},
    }

    try:
        data = _post_json(endpoint, headers=headers, payload=payload)

        # Normalize response
        created = data.get("created")
        items = data.get("data") or []
        if not items:
            raise RuntimeError(f"empty data: {data}")

        out_dir = Path(args.out_dir).expanduser().resolve()
        ts = int(time.time())
        slug = _sanitize_filename(args.prompt)

        first = items[0]
        out_file: Optional[Path] = None
        src_url: Optional[str] = None

        if "url" in first and first["url"]:
            src_url = first["url"]
            out_file = out_dir / f"{ts}_{slug}.png"
            _download(src_url, out_file)
        elif "b64_json" in first and first["b64_json"]:
            b64 = first["b64_json"]
            raw = base64.b64decode(b64)
            out_file = out_dir / f"{ts}_{slug}.png"
            out_file.parent.mkdir(parents=True, exist_ok=True)
            out_file.write_bytes(raw)
        else:
            # Some vendors return list of base64 strings in 'data'
            # Try common fallback keys
            if isinstance(first, str) and first.startswith("data:image"):
                # data URL
                m = re.match(r"data:image/\w+;base64,(.*)", first)
                if m:
                    raw = base64.b64decode(m.group(1))
                    out_file = out_dir / f"{ts}_{slug}.png"
                    out_file.parent.mkdir(parents=True, exist_ok=True)
                    out_file.write_bytes(raw)
                else:
                    raise RuntimeError(f"unknown data url: {first[:64]}")
            else:
                raise RuntimeError(f"unrecognized response item: {first}")

        public_base = _env("ARK_T2I_PUBLIC_BASE")
        public_url = None
        try:
            # Default mapping for this workspace: /mnt/开发/images -> https://co.rotes.shop/images/
            if public_base:
                public_base = public_base.rstrip("/") + "/"
                public_url = public_base + out_file.name
            else:
                out_dir_resolved = out_dir
                if str(out_dir_resolved) == "/mnt/开发/images":
                    public_url = f"https://co.rotes.shop/images/{out_file.name}"
        except Exception:
            public_url = None

        print(
            json.dumps(
                {
                    "status": "success",
                    "created": created,
                    "file": str(out_file) if out_file else None,
                    "public_url": public_url,
                    "url": src_url,
                    "model": args.model,
                    "size": args.size,
                },
                ensure_ascii=False,
            )
        )
        return 0

    except Exception as e:
        print(json.dumps({"status": "error", "message": str(e)}, ensure_ascii=False))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
