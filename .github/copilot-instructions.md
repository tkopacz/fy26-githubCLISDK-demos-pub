# Copilot instructions

This repository is a **workshop-style collection of ~20 C#/.NET demos** showing how to embed the
**GitHub Copilot SDK** (`GitHub.Copilot.SDK`) in applications: minimal sessions, streaming, custom
tools, permission hooks, MCP, platform memory, agent workflows, remote runtime, and BYOK.

The authoritative narrative (per-demo descriptions and run commands) lives in `README.md`, which is
**written in Polish** — read it for context on any individual demo.

## Build, test, lint

- **Target framework is `net10.0`** (see `Directory.Build.props`). `dotnet --version` must be 10.0+.
- One solution groups everything: `CopilotSDK.Demos.slnx`.

```powershell
dotnet build CopilotSDK.Demos.slnx          # build all
dotnet test  CopilotSDK.Demos.slnx          # run all tests
dotnet run --project demos/01-hello-session/HelloSession   # run a single demo
```

Run a single test by fully-qualified name filter:

```powershell
dotnet test --filter "FullyQualifiedName~SessionChannelBridgeTests.ReadAllAsync_YieldsDeltaTokens_WhenDeltaEventsEmitted"
```

- **`TreatWarningsAsErrors` is `true`** (`Directory.Build.props`) — any compiler warning fails the
  build. Keep builds warning-clean. `Nullable` and `ImplicitUsings` are enabled solution-wide.
- Tests use **xUnit + FluentAssertions + NSubstitute + Xunit.SkippableFact**.
- **Integration tests require a `.env` file with `GITHUB_TOKEN`**; they self-skip when it is absent
  (via `Xunit.SkippableFact`). Unit-style tests have no external dependency.

## Project layout conventions (important, easy to break)

- **`.csproj` file names are deliberately unusual** — they carry workshop ordering prefixes and tags,
  e.g. `01 HelloSession.csproj`, `02 CodeReview.csproj`, `StreamingEvents (OPT).csproj`,
  `04 GuardedCopilotCli.csproj`. **Do not "clean up" or rename these** — `.slnx`, project references,
  and `dotnet run --project` paths all depend on the exact names (spaces and numbers included).
- The `_BETA/` folder holds demos **intentionally excluded** from the solution — do not assume they
  build as part of the main solution.
- Every demo is a console `Exe` that references `shared/CopilotSDK.Shared` rather than the SDK
  directly; shared infrastructure is the single source of truth for client/session setup.

## Architecture (the big picture)

The demos all reuse a small shared layer in `shared/CopilotSDK.Shared/Infrastructure`:

- **`CopilotClientFactory`** — owns construction of `CopilotClient` (one per process/app, treated as
  infrastructure), model selection, BYOK provider config, and remote-runtime state files. All demos
  go through it instead of `new CopilotClient(...)`.
- **`SessionHelper.SendAndWaitAsync`** — bridges the SDK's event-driven turn model into a simple
  `await`: it calls `session.SendAsync` then waits for `SessionIdleEvent`, returning the final
  `AssistantMessageEvent` content. This is the canonical request/response pattern across demos.
- **`SessionChannelBridge`** — maps SDK callbacks to `IAsyncEnumerable<string>` for SSE streaming
  (used by the ASP.NET demos 10 and 18).
- **`EventLogger` / `TelemetryObserver`** — attach to a session to surface lifecycle events and
  JSON-RPC telemetry.

Core SDK mental model reflected throughout:
- One `CopilotClient` per process; a **fresh `CopilotSession` per independent interaction/request**.
  A session owns its model, system message, tools, MCP servers, permissions, and conversation history.
- Tools are **explicitly registered** (`AIFunctionFactory.Create` from `Microsoft.Extensions.AI`);
  the model only sees exposed functions.
- **`OnPermissionRequest`** (plus `SessionHooks.OnPreToolUse`/`OnPostToolUse`) is the host-side
  checkpoint before sensitive operations (file writes, memory, MCP). Demo 20 (`GuardedCopilotCli`)
  shows defense-in-depth: the same path check is enforced at the permission handler, the pre-tool
  hook, **and** inside the tool implementation — host-side C# enforcement, not prompt instructions.

## Conventions specific to this codebase

- **Code comments and user-facing console strings are in Polish; identifiers are English.** Match
  this when editing existing files — keep new comments/UI text in Polish to stay consistent.
- **UI is rendered via `Spectre.Console`** through the shared `ConsoleRenderer` (banners, rules,
  spinners). Prefer it over raw `Console.WriteLine` in demos.
- Experimental SDK permission APIs are wrapped in `#pragma warning disable GHCP001` / `restore` —
  preserve these suppressions when touching permission/hook code (warnings are errors).
- Configuration is environment-variable driven, resolved inside `CopilotClientFactory`:
  `COPILOT_MODEL` (override model), `BYOK_MODE=1` + `BYOK_PROVIDER`/`BYOK_API_KEY`/`BYOK_BASE_URL`
  (bring-your-own-key routing), and `GITHUB_TOKEN` for GitHub MCP/API demos (12, 18).
- Pinned transitive dependency `Nerdbank.MessagePack 1.2.4` appears in csproj files to avoid a known
  advisory — keep the pin when editing those `<ItemGroup>`s.

## MCP servers

`.vscode/mcp.json` configures two MCP servers matching the demos:
- **github** (hosted HTTP, `https://api.githubcopilot.com/mcp/`) — used by demos 12 and 18, which
  otherwise drive the GitHub MCP server via `GITHUB_TOKEN`.
- **filesystem** (`npx @modelcontextprotocol/server-filesystem`, scoped to the workspace) — the same
  server demo 11 launches over stdio.
