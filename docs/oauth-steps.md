# OAuth Token Refresh Guide

Both Zomato and Swiggy MCP servers use OAuth 2.0 with PKCE (S256). Tokens expire periodically and need to be refreshed.

| Service | Token Lifetime | Scope |
|---------|---------------|-------|
| Zomato  | ~30 days      | `offline openid` |
| Swiggy  | ~5 days       | One token for Food, Instamart, and Dineout |

## Prerequisites

- `python3`, `curl`, `openssl` available (standard on WSL)
- Working directory: `src/WoofAgent.Cli`

## Zomato Token Refresh

### 1. Register a client

```bash
curl -s -X POST https://mcp-server.zomato.com/register \
  -H "Content-Type: application/json" \
  -d '{
    "client_name": "WoofAgent",
    "redirect_uris": ["https://oauth.pstmn.io/v1/callback"],
    "grant_types": ["authorization_code"],
    "response_types": ["code"],
    "token_endpoint_auth_method": "none"
  }'
```

Note the `client_id` from the response.

### 2. Generate PKCE values

```bash
python3 -c "
import secrets, hashlib, base64
v = secrets.token_urlsafe(32)
c = base64.urlsafe_b64encode(hashlib.sha256(v.encode()).digest()).rstrip(b'=').decode()
print(f'VERIFIER={v}')
print(f'CHALLENGE={c}')
"
```

### 3. Open authorization URL in browser

Build the URL (replace `CLIENT_ID`, `CHALLENGE`):

```
https://mcp-server.zomato.com/authorize?response_type=code&client_id=CLIENT_ID&redirect_uri=https%3A%2F%2Foauth.pstmn.io%2Fv1%2Fcallback&code_challenge=CHALLENGE&code_challenge_method=S256&state=RANDOM_STATE&scope=mcp%3Atools
```

Use `python3 -c "import urllib.parse; ..."` to URL-encode properly. The `state` and `redirect_uri` must be URL-encoded.

Log in, authorize, and copy the `code` from the callback URL.

### 4. Exchange code for token

```bash
curl -s -X POST https://mcp-server.zomato.com/token \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=authorization_code&code=AUTH_CODE&code_verifier=VERIFIER&client_id=CLIENT_ID&redirect_uri=https://oauth.pstmn.io/v1/callback"
```

### 5. Store the token

```bash
cd src/WoofAgent.Cli
dotnet user-secrets set "McpServers:Zomato:AccessToken" "TOKEN_VALUE"
```

## Swiggy Token Refresh

Swiggy access tokens last **5 days** and Swiggy issues **no refresh token**. Sign-in needs phone + OTP on Swiggy's own page, and Swiggy's docs say the OTP endpoints are internal and not for third-party clients. So the OTP step can't be fully automated. `scripts/swiggy_token.py` makes it a one-minute step and automates everything else.

### One-time: register the client

Swiggy requires the client to be named `"Claude"` and does **not** allow Postman callback URLs:

```bash
curl -s -X POST https://mcp.swiggy.com/auth/register \
  -H "Content-Type: application/json" \
  -d '{
    "client_name": "Claude",
    "redirect_uris": ["http://localhost:8765/callback"],
    "grant_types": ["authorization_code"],
    "response_types": ["code"],
    "token_endpoint_auth_method": "none"
  }'
```

The `client_id` is always `swiggy-mcp`.

### Get a new token (every time it expires)

```bash
python3 scripts/swiggy_token.py login
```

The script opens the browser. Sign in with the phone number and OTP. The script catches the callback on `localhost:8765`, exchanges the code (PKCE), and writes `~/.woof-agent/swiggy-token.json` (mode 600).

- **No user-secrets step and no restart.** WoofAgent reads the token file on every request (`BearerTokenHandler`), so a running session uses the new token on its next MCP call.
- The token file takes precedence over `McpServers:Swiggy:AccessToken`. The user-secret is still used as a fallback when the file is missing.
- Override the path with `McpServers:Swiggy:TokenFile` in `appsettings.json` and `WOOF_SWIGGY_TOKEN_FILE` for the script.
- Headless machine: run it with `--no-browser`, then open the printed URL in a browser **on the same machine** (the redirect goes to `localhost`).

### Check expiry and get a push alert

```bash
python3 scripts/swiggy_token.py status                  # exit 0 OK, 1 expiring soon, 2 expired/missing
python3 scripts/swiggy_token.py status --warn-hours 30 --notify
```

With `--notify`, an alert is pushed through [ntfy](https://ntfy.sh). Install the ntfy app on your phone and subscribe to a hard-to-guess topic. Then set `WOOF_NTFY_TOPIC` to that topic (and `WOOF_NTFY_SERVER` if you self-host ntfy).

Example crontab for the demo machine (checks every 3 hours):

```cron
0 */3 * * * WOOF_NTFY_TOPIC=woof-demo-<random> python3 /path/to/woof-agent/scripts/swiggy_token.py status --warn-hours 30 --notify
```

### Running a multi-day demo (e.g. a tech fair)

1. Use a dedicated demo Swiggy account, with its phone held by whoever staffs the booth.
2. Run `swiggy_token.py login` **each morning before doors open**. Every login returns a fresh 5-day token, so it can never expire during the day.
3. Keep the cron alert above as a safety net in case a morning login is missed.
4. For a long-term fix, apply for partner access through the [Swiggy Builders Club](https://mcp.swiggy.com/builders/) and ask whether refresh tokens or longer-lived tokens are available.

## Verify tokens are working

```bash
echo "exit" | dotnet run --project src/WoofAgent.Cli -- --zomato
echo "exit" | dotnet run --project src/WoofAgent.Cli -- --swiggy
echo "exit" | dotnet run --project src/WoofAgent.Cli -- --swiggy-instamart
echo "exit" | dotnet run --project src/WoofAgent.Cli -- --swiggy-dineout
```

Each should print `Discovered N tools` without errors.

## OAuth discovery endpoints

These return supported scopes, grant types, and endpoints:

```bash
curl -s https://mcp-server.zomato.com/.well-known/oauth-authorization-server | python3 -m json.tool
curl -s https://mcp.swiggy.com/.well-known/oauth-authorization-server | python3 -m json.tool
```
