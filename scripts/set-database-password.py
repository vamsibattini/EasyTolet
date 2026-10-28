#!/usr/bin/env python3
"""Store a service password in .NET user-secrets without command-line/history exposure."""
import argparse
import getpass
import json
import pathlib
import subprocess
import sys

root = pathlib.Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("service", nargs="?", default="EasyTolet.UserService")
parser.add_argument("--logging", action="store_true", help="Set a dedicated exception logging database password")
args = parser.parse_args()
service = args.service
project = root / "src" / service / f"{service}.csproj"
if not project.is_file() or not service.startswith("EasyTolet.") or "/" in service or "\\" in service:
    sys.exit("Pass an existing service name, for example EasyTolet.UserService.")
password = getpass.getpass(f"MySQL password for {service}: ")
if not password:
    sys.exit("Password cannot be empty.")
subprocess.run(
    ["dotnet", "user-secrets", "set", "--project", str(project)],
    input=json.dumps({("ExceptionLogging:Database:Password" if args.logging else "Database:Password"): password}), text=True, check=True,
)
