#!/usr/bin/env python3
"""Generate Napisy24 activation codes.

The private key stays outside the repository. The plugin only embeds the
matching public key, so a public repository cannot be used to mint codes.

Examples:
    # one server (paste the server id from the plugin settings page)
    ./tools/make-activation-code.py --server-id e95f163bb9314f8e829305a15fc9383a --name jellykulik

    # a shared code that works on any server
    ./tools/make-activation-code.py --any --name friends --days 365

    # generate a new key pair (writes activation-private.pem / activation-public.pem)
    ./tools/make-activation-code.py --keygen
"""
from __future__ import annotations

import argparse
import base64
import json
import os
import stat
import sys
import time
from pathlib import Path

try:
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import ec
except ImportError:  # pragma: no cover
    print("Missing dependency: pip install cryptography", file=sys.stderr)
    raise SystemExit(2)

PREFIX = "N24v1"
DEFAULT_KEY = Path.home() / ".local/share/signing/jellyfin-plugin-napisy24/activation-private.pem"


def b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).decode("ascii").rstrip("=")


def find_key(explicit: str | None) -> Path:
    if explicit:
        return Path(explicit).expanduser()
    if DEFAULT_KEY.exists():
        return DEFAULT_KEY
    raise SystemExit(
        f"Private key not found at {DEFAULT_KEY}. Run with --keygen or pass --key."
    )


def keygen(out_dir: Path) -> None:
    out_dir.mkdir(parents=True, exist_ok=True)
    private_path = out_dir / "activation-private.pem"
    public_path = out_dir / "activation-public.pem"

    key = ec.generate_private_key(ec.SECP256R1())
    private_pem = key.private_bytes(
        encoding=serialization.Encoding.PEM,
        format=serialization.PrivateFormat.PKCS8,
        encryption_algorithm=serialization.NoEncryption(),
    )
    public_pem = key.public_key().public_bytes(
        encoding=serialization.Encoding.PEM,
        format=serialization.PublicFormat.SubjectPublicKeyInfo,
    )
    public_der = key.public_key().public_bytes(
        encoding=serialization.Encoding.DER,
        format=serialization.PublicFormat.SubjectPublicKeyInfo,
    )

    private_path.write_bytes(private_pem)
    private_path.chmod(stat.S_IRUSR | stat.S_IWUSR)
    public_path.write_bytes(public_pem)

    print(f"private key: {private_path}")
    print(f"public key : {public_path}")
    print("embed this in Activation/ActivationService.cs PublicKeyBase64:")
    print(base64.b64encode(public_der).decode("ascii"))


def make_code(key_path: Path, server_id: str, name: str, days: int) -> str:
    key = serialization.load_pem_private_key(key_path.read_bytes(), password=None)
    if not isinstance(key, ec.EllipticCurvePrivateKey):
        raise SystemExit(f"{key_path} is not an EC private key")

    exp = int(time.time()) + days * 86400 if days > 0 else 0
    payload = json.dumps(
        {"sid": server_id, "name": name, "exp": exp},
        separators=(",", ":"),
        sort_keys=True,
    ).encode("utf-8")
    signature = key.sign(payload, ec.ECDSA(hashes.SHA256()))
    return f"{PREFIX}.{b64url(payload)}.{b64url(signature)}"


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate Napisy24 activation codes")
    parser.add_argument("--key", help="path to the private EC key (PEM)")
    parser.add_argument("--server-id", help="Jellyfin server id the code is bound to")
    parser.add_argument("--any", action="store_true", help="make a shared code for any server")
    parser.add_argument("--name", default="", help="label stored in the code (e.g. server name)")
    parser.add_argument("--days", type=int, default=0, help="validity in days (0 = no expiry)")
    parser.add_argument("--keygen", action="store_true", help="generate a new key pair")
    parser.add_argument(
        "--out-dir",
        default=str(Path.home() / ".local/share/signing/jellyfin-plugin-napisy24"),
        help="directory for --keygen",
    )
    args = parser.parse_args()

    if args.keygen:
        keygen(Path(args.out_dir).expanduser())
        return

    if args.any:
        server_id = "*"
    elif args.server_id:
        server_id = args.server_id.strip()
    else:
        parser.error("pass --server-id <id> or --any")

    key_path = find_key(args.key)
    code = make_code(key_path, server_id, args.name, args.days)

    print(code)
    print()
    print(f"server : {server_id}")
    print(f"name   : {args.name or '-'}")
    print(f"expires: {'never' if args.days <= 0 else f'{args.days} days'}")


if __name__ == "__main__":
    main()
