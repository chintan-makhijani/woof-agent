#!/usr/bin/env python3
"""Swiggy MCP token helper.

Swiggy MCP access tokens last 5 days and there is no refresh token, so a human
has to sign in again with phone + OTP on Swiggy's own page. This script makes
that a one-minute step and automates everything around it:

  login   Run the OAuth 2.1 + PKCE flow, open the browser, catch the callback,
          exchange the code and write the token to the token file. A running
          WoofAgent picks up the new token on its next request (no restart).
  status  Print how long the current token has left. Exit code 0 = OK,
          1 = expiring within --warn-hours, 2 = expired or missing.
          With --notify, pushes an alert to your phone via ntfy.sh
          (set WOOF_NTFY_TOPIC) so nobody is surprised mid-demo.

Only the Python standard library is used.
"""

import argparse
import base64
import hashlib
import json
import os
import secrets
import sys
import threading
import urllib.parse
import urllib.request
import webbrowser
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, HTTPServer

AUTH_URL = "https://mcp.swiggy.com/auth/authorize"
TOKEN_URL = "https://mcp.swiggy.com/auth/token"
CLIENT_ID = "swiggy-mcp"
DEFAULT_PORT = 8765
DEFAULT_TOKEN_FILE = "~/.woof-agent/swiggy-token.json"
DOCUMENTED_LIFETIME = timedelta(days=5)


def token_file_path(arg):
    return os.path.expanduser(arg or os.environ.get("WOOF_SWIGGY_TOKEN_FILE", DEFAULT_TOKEN_FILE))


def jwt_expiry(token):
    parts = token.split(".")
    if len(parts) != 3:
        return None
    try:
        payload = parts[1] + "=" * (-len(parts[1]) % 4)
        exp = json.loads(base64.urlsafe_b64decode(payload)).get("exp")
        return datetime.fromtimestamp(exp, timezone.utc) if exp else None
    except (ValueError, json.JSONDecodeError):
        return None


def write_token_file(path, token_response):
    now = datetime.now(timezone.utc)
    token = token_response["access_token"]
    if "expires_in" in token_response:
        expires_at = now + timedelta(seconds=int(token_response["expires_in"]))
    else:
        expires_at = jwt_expiry(token) or now + DOCUMENTED_LIFETIME

    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + ".tmp"
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w") as f:
        json.dump({
            "access_token": token,
            "obtained_at": now.isoformat(),
            "expires_at": expires_at.isoformat(),
        }, f, indent=2)
    os.replace(tmp, path)  # atomic, so the app never reads a half-written file
    return expires_at


def exchange_code(code, verifier, redirect_uri):
    data = urllib.parse.urlencode({
        "grant_type": "authorization_code",
        "code": code,
        "code_verifier": verifier,
        "client_id": CLIENT_ID,
        "redirect_uri": redirect_uri,
    }).encode()
    req = urllib.request.Request(TOKEN_URL, data=data,
                                 headers={"Content-Type": "application/x-www-form-urlencoded"})
    with urllib.request.urlopen(req, timeout=15) as resp:
        return json.loads(resp.read().decode())


def cmd_login(args):
    path = token_file_path(args.token_file)
    redirect_uri = f"http://localhost:{args.port}/callback"
    verifier = secrets.token_urlsafe(48)
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
    state = secrets.token_urlsafe(16)

    url = AUTH_URL + "?" + urllib.parse.urlencode({
        "response_type": "code",
        "client_id": CLIENT_ID,
        "redirect_uri": redirect_uri,
        "code_challenge": challenge,
        "code_challenge_method": "S256",
        "state": state,
    })

    result = {}

    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            parsed = urllib.parse.urlparse(self.path)
            if parsed.path != "/callback":
                self.send_response(404)
                self.end_headers()
                return
            params = urllib.parse.parse_qs(parsed.query)
            self.send_response(200)
            self.send_header("Content-Type", "text/html")
            self.end_headers()
            try:
                if params.get("state", [None])[0] != state:
                    raise ValueError("state mismatch")
                if "code" not in params:
                    raise ValueError(params.get("error_description", params.get("error", ["no code"]))[0])
                expires_at = write_token_file(path, exchange_code(params["code"][0], verifier, redirect_uri))
                result["expires_at"] = expires_at
                self.wfile.write(b"<h1>Swiggy token saved. You can close this tab.</h1>")
            except Exception as e:  # report any failure in the browser and the terminal
                result["error"] = str(e)
                self.wfile.write(f"<h1>Login failed: {e}</h1>".encode())
            threading.Thread(target=self.server.shutdown).start()

        def log_message(self, *a):
            pass

    server = HTTPServer(("localhost", args.port), Handler)
    print("Sign in to Swiggy (phone + OTP) in the browser window.")
    print(f"If it does not open, visit:\n\n{url}\n")
    if not args.no_browser:
        webbrowser.open(url)
    server.serve_forever()

    if "error" in result:
        print(f"Login failed: {result['error']}", file=sys.stderr)
        return 2
    print(f"Token saved to {path}")
    print(f"Valid until {result['expires_at'].astimezone():%a %d %b %H:%M %Z}. "
          "Running WoofAgent processes will use it on their next request.")
    return 0


def notify(message, priority):
    topic = os.environ.get("WOOF_NTFY_TOPIC")
    if not topic:
        print("--notify given but WOOF_NTFY_TOPIC is not set; skipping push.", file=sys.stderr)
        return
    server = os.environ.get("WOOF_NTFY_SERVER", "https://ntfy.sh").rstrip("/")
    req = urllib.request.Request(f"{server}/{topic}", data=message.encode(), headers={
        "Title": "WoofAgent: Swiggy token",
        "Priority": priority,
        "Tags": "warning",
    })
    try:
        urllib.request.urlopen(req, timeout=10).close()
    except OSError as e:
        print(f"Push notification failed: {e}", file=sys.stderr)


def cmd_status(args):
    path = token_file_path(args.token_file)
    try:
        with open(path) as f:
            data = json.load(f)
        expires_at = datetime.fromisoformat(data["expires_at"])
    except (OSError, ValueError, KeyError):
        msg = f"No valid Swiggy token at {path}. Run: python3 scripts/swiggy_token.py login"
        print(msg)
        if args.notify:
            notify(msg, "urgent")
        return 2

    remaining = expires_at - datetime.now(timezone.utc)
    hours = remaining.total_seconds() / 3600
    when = expires_at.astimezone().strftime("%a %d %b %H:%M")

    if hours <= 0:
        msg = f"Swiggy token EXPIRED at {when}. Re-login now: python3 scripts/swiggy_token.py login"
        code, priority = 2, "urgent"
    elif hours <= args.warn_hours:
        msg = f"Swiggy token expires in {hours:.1f}h ({when}). Re-login: python3 scripts/swiggy_token.py login"
        code, priority = 1, "high"
    else:
        print(f"Swiggy token OK: {hours / 24:.1f} days left (expires {when}).")
        return 0

    print(msg)
    if args.notify:
        notify(msg, priority)
    return code


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--token-file", help=f"default: $WOOF_SWIGGY_TOKEN_FILE or {DEFAULT_TOKEN_FILE}")
    sub = parser.add_subparsers(dest="command", required=True)

    login = sub.add_parser("login", help="sign in and save a fresh token")
    login.add_argument("--port", type=int, default=DEFAULT_PORT,
                       help="must match the redirect URI registered with Swiggy (default 8765)")
    login.add_argument("--no-browser", action="store_true", help="only print the URL")
    login.set_defaults(func=cmd_login)

    status = sub.add_parser("status", help="check remaining token lifetime")
    status.add_argument("--warn-hours", type=float, default=24)
    status.add_argument("--notify", action="store_true", help="push an alert via ntfy when expiring/expired")
    status.set_defaults(func=cmd_status)

    args = parser.parse_args()
    sys.exit(args.func(args))


if __name__ == "__main__":
    main()
