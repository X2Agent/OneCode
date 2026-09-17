# OneCode .NET

A production-grade CLI AI coding assistant built on .NET 10, featuring a Terminal.Gui v2 full-screen TUI, the Microsoft Agent Framework (MAF) orchestration pipeline, and a Hyperlight sandbox.

## Highlights

- **Four working modes** — BUILD / PLAN / TEAM / GOAL, switchable with `Tab`
- **44 slash commands · 31 built-in tools** — file ops, LSP, web, agents, cron, Git worktree, MCP
- **Safety-first execution** — 8 permission modes, command classifiers, hooks, editable transactions
- **Cross-platform** — self-contained single-file binaries for Windows / Linux / macOS (x64 + ARM64)

## Install

**Windows (PowerShell)**

```powershell
irm https://raw.githubusercontent.com/X2Agent/OneCode/main/scripts/install.ps1 | iex
```

**Linux / macOS**

```bash
curl -fsSL https://raw.githubusercontent.com/X2Agent/OneCode/main/scripts/install.sh | bash
```

## Quick start

```bash
onecode                        # launch the interactive TUI (BUILD mode)
onecode "fix the CSS bug"      # start with an initial prompt
onecode --version
```

## Documentation

Full documentation is maintained in [README_CN.md](README_CN.md) (Chinese) and the [docs/](docs/) directory — commands reference, settings reference, skills guide, keybindings, ADRs, and design plans.

## License

The contents of this repository are provided for technical research and educational purposes only. See [README_CN.md](README_CN.md) for the full disclaimer.
