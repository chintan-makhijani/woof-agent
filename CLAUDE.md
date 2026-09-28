# Woof-Agent

Multi-agent system using Microsoft Semantic Kernel and .NET 8, running as a CLI tool on WSL. Uses Google A2UI for optional UI rendering of agent responses.

## Tech Stack

- **.NET 8** — C# with standard conventions (upgrade to .NET 9 when SDK is installed)
- **Microsoft Semantic Kernel 1.70.0** — agent orchestration, plugins, and LLM connectors
  - Reference samples: https://github.com/microsoft/semantic-kernel/tree/main/dotnet/samples/Concepts
- **LLM Providers** — OpenAI and Google Gemini (multi-provider, swappable via config)
  - OpenAI connector: stable (1.70.0)
  - Google/Gemini connector: alpha (1.70.0-alpha) — use with `#pragma warning disable SKEXP0070`
- **Google A2UI** — for structured UI response generation by agents
  - Reference repo: https://github.com/google/A2UI
  - Official docs: https://a2ui.org/
- **Runtime** — WSL (Ubuntu) CLI environment

## Key Commands

- Build: `dotnet build`
- Run (A2UI mode): `dotnet run --project src/WoofAgent.Cli`
- Run (text mode): `dotnet run --project src/WoofAgent.Cli -- --text`
- Run (Zomato mode): `dotnet run --project src/WoofAgent.Cli -- --zomato`
- Run (Swiggy Food): `dotnet run --project src/WoofAgent.Cli -- --swiggy`
- Run (Swiggy Instamart): `dotnet run --project src/WoofAgent.Cli -- --swiggy-instamart`
- Run (Swiggy Dineout): `dotnet run --project src/WoofAgent.Cli -- --swiggy-dineout`
- Test: `dotnet test`
- Clean: `dotnet clean && dotnet build`

## Project Structure

```
WoofAgent.sln
├── src/
│   ├── WoofAgent.Cli/          # Entry point, DI setup, CLI loop
│   ├── WoofAgent.Core/         # Agent abstractions, agent implementations
│   │   ├── Agents/
│   │   │   ├── IAgent.cs           # Agent interface
│   │   │   ├── HelloWorldAgent.cs  # Simple prompt agent (no function calling)
│   │   │   ├── SmartFridgeAgent.cs # Function calling agent (plain text output)
│   │   │   ├── A2UIFridgeAgent.cs  # Function calling + A2UI JSON output
│   │   │   ├── ZomatoAgent.cs      # MCP-powered food ordering agent
│   │   │   └── SwiggyAgent.cs      # MCP-powered Swiggy agent (Food/Instamart/Dineout)
│   │   └── Plugins/
│   │       └── FridgePlugin.cs     # Mock smart fridge functions
│   ├── WoofAgent.Providers/    # SK connector extensions (OpenAI, Gemini, OpenRouter)
│   └── WoofAgent.Shared/       # DTOs, configuration models, utilities
│       └── Auth/                   # FileTokenStore + BearerTokenHandler (hot-reloaded MCP tokens)
├── scripts/
│   └── swiggy_token.py         # Swiggy OAuth login + expiry check/alerts
```

**Project References:**
- Cli → Core, Providers, Shared
- Core → Shared
- Providers → Core, Shared

## Architecture

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for detailed diagrams and flow documentation.

### Agent Types

| Agent | Function Calling | Output Format | Use Case |
|-------|------------------|---------------|----------|
| `HelloWorldAgent` | No | Plain text | Simple prompts, testing |
| `SmartFridgeAgent` | Yes (Auto) | Plain text | CLI interaction, debugging |
| `A2UIFridgeAgent` | Yes (Auto) | Text + A2UI JSON | Tizen UI rendering |
| `ZomatoAgent` | Yes (Auto, MCP) | Plain text | Food ordering via Zomato MCP |
| `SwiggyAgent` | Yes (Auto, MCP) | Plain text | Swiggy Food/Instamart/Dineout via MCP |

### Key Patterns

- All agents implement `IAgent` (Name + InvokeAsync)
- Providers swapped via `KernelBuilderExtensions.AddLlmProvider()` reading `LlmSettings`
- A2UI agent outputs two-part response: conversational text + `---a2ui---` + JSON
- FridgePlugin has 7 mock functions decorated with `[KernelFunction]` for auto function calling
- MCP agents (Zomato, Swiggy) use tools discovered at runtime — registered on the kernel externally via `AsKernelFunction()`

## Config & Secrets

**Approach: appsettings.json + dotnet user-secrets**

- `appsettings.json` — non-sensitive config (default provider, model names)
- `dotnet user-secrets` — API keys (never committed)

**Setting up secrets:**
```bash
cd src/WoofAgent.Cli
dotnet user-secrets set "LlmProviders:OpenAi:ApiKey" "sk-..."
dotnet user-secrets set "LlmProviders:Gemini:ApiKey" "..."
dotnet user-secrets set "McpServers:Zomato:AccessToken" "your-token-here"
dotnet user-secrets set "McpServers:Swiggy:AccessToken" "your-token-here"
```

**Getting MCP OAuth tokens (one-time setup):**

Both Zomato and Swiggy use OAuth 2.0 with PKCE. Register a client, then do the auth code flow:

1. Register client: `curl -X POST https://<server>/register -H "Content-Type: application/json" -d '{"client_name":"WoofAgent","redirect_uris":["https://oauth.pstmn.io/v1/callback"],...}'`
2. Generate PKCE code_verifier + code_challenge (S256)
3. Open the authorization URL in a browser, log in
4. Copy the `code` from the callback URL
5. Exchange for token: `curl -X POST https://<server>/token -d 'grant_type=authorization_code&code=...&code_verifier=...'`
6. Store with `dotnet user-secrets` command above

**Swiggy token automation:** Swiggy tokens expire every 5 days, with no refresh token. Run `python3 scripts/swiggy_token.py login`, which writes `~/.woof-agent/swiggy-token.json`. The app re-reads that file on every request, so no restart is needed. `swiggy_token.py status --notify` sends an expiry alert. See [docs/oauth-steps.md](docs/oauth-steps.md).

OAuth endpoints:
- **Zomato**: auth `https://mcp-server.zomato.com/authorize`, token `https://mcp-server.zomato.com/token`, register `https://mcp-server.zomato.com/register`
- **Swiggy**: auth `https://mcp.swiggy.com/auth/authorize`, token `https://mcp.swiggy.com/auth/token`, register `https://mcp.swiggy.com/auth/register` (one token works for all 3 Swiggy services)

**Switching providers:**
Edit `appsettings.json`:
```json
{
  "LlmProviders": {
    "DefaultProvider": "OpenAI"  // or "Gemini"
  }
}
```

## Standards

- Standard C# conventions (PascalCase for public members, camelCase for locals, `I` prefix for interfaces)
- Async/await for all I/O and LLM calls
- Dependency injection via `Microsoft.Extensions.DependencyInjection`
- Keep Semantic Kernel abstractions over raw LLM API calls — don't bypass SK unless necessary

## Testing

- Framework TBD — propose when first tests are needed
- Prefer integration tests for agent behavior, unit tests for plugins/utilities

## Gotchas & Reminders

- This project runs on WSL — use Linux-compatible paths and commands
- Semantic Kernel is evolving rapidly — check NuGet for latest stable packages before adding dependencies
- When unsure about SK APIs, reference the official samples repo linked above rather than guessing
- Google connector is alpha — suppress experimental warning with `#pragma warning disable SKEXP0070`
- A2UI is v0.9 (public preview) — verify schema against https://a2ui.org/ before implementing

