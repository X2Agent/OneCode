---
name: OneCode Dark
description: >
  Dark-first terminal UI design system for a multi-mode AI coding assistant.
  Constrained to the ANSI 16-color palette of Terminal.Gui v2.
  OKLCH hex values represent design intent; Terminal.Gui colors are the
  closest 16-color approximation used at render time.
colors:
  # ── Primary ────────────────────────────────────
  primary: "#5b8dee"
  # ── Backgrounds ──────────────────────────────────
  bg-root: "#12151a"
  bg-surface: "#181b22"
  bg-elevated: "#1e212a"
  bg-input: "#0f1218"
  # ── Brand & Semantic ─────────────────────────────
  accent: "#5b8dee"
  accent-teal: "#14B8A6"
  success: "#4caf84"
  warning: "#e5b14c"
  error: "#e0556a"
  info: "#58A6FF"
  # ── Text ─────────────────────────────────────────
  text-primary: "#d4d8e0"
  text-secondary: "#9298a4"
  text-muted: "#5c6270"
  # ── Borders ──────────────────────────────────────
  border: "#2a2e3a"
  border-active: "#3d5080"
  # ── Diff ─────────────────────────────────────────
  diff-add: "#4caf84"
  diff-del: "#e0556a"
  diff-hunk: "#5bb8c8"
  diff-context: "#9298a4"
  # ── Mode identifiers ─────────────────────────────
  mode-build: "#4caf84"
  mode-plan: "#5b8dee"
  mode-team: "#a386d8"
  mode-goal: "#5bb8c8"
  # ── Agent 8-color system ─────────────────────────
  agent-orchestrator: "#a386d8"
  agent-researcher: "#5b8dee"
  agent-planner: "#4caf84"
  agent-executor: "#e08b5c"
  agent-reviewer: "#e5b14c"
  agent-tester: "#e0556a"
  agent-debugger: "#e07ba5"
  agent-assistant: "#5bb8c8"
  # ── Message role ─────────────────────────────────
  user-message: "#5bb8c8"
  assistant-message: "#d4d8e0"
  tool-use: "#e5b14c"
  tool-result: "#c4a03c"
  system-message: "#a386d8"
typography:
  body:
    fontFamily: JetBrains Mono, Cascadia Code, Fira Code, Consolas, monospace
    fontSize: 13px
    lineHeight: 1.55
  status-bar:
    fontFamily: JetBrains Mono, Cascadia Code, Consolas, monospace
    fontSize: 10.5px
  heading:
    fontFamily: JetBrains Mono, Cascadia Code, Consolas, monospace
    fontSize: 15px
    fontWeight: 700
spacing:
  xs: 2px
  sm: 4px
  md: 8px
  lg: 12px
  xl: 16px
components:
  status-bar:
    backgroundColor: "{colors.bg-surface}"
    textColor: "{colors.text-secondary}"
    padding: 0 8px
  border-default:
    backgroundColor: "{colors.border}"
  border-focus:
    backgroundColor: "{colors.border-active}"
  input-bar:
    backgroundColor: "{colors.bg-input}"
    textColor: "{colors.text-primary}"
    padding: 0 8px
  input-mode-tag-build:
    backgroundColor: "{colors.mode-build}"
    textColor: "{colors.bg-root}"
  input-mode-tag-plan:
    backgroundColor: "{colors.mode-plan}"
    textColor: "{colors.bg-root}"
  input-mode-tag-team:
    backgroundColor: "{colors.mode-team}"
    textColor: "{colors.bg-root}"
  input-mode-tag-goal:
    backgroundColor: "{colors.mode-goal}"
    textColor: "{colors.bg-root}"
  message-user:
    textColor: "{colors.user-message}"
  message-assistant:
    textColor: "{colors.assistant-message}"
  message-tool-call:
    textColor: "{colors.tool-use}"
  message-tool-result:
    textColor: "{colors.tool-result}"
  message-system:
    textColor: "{colors.system-message}"
  status-info:
    textColor: "{colors.info}"
  status-warning:
    textColor: "{colors.warning}"
  status-success:
    textColor: "{colors.success}"
  thinking-block:
    textColor: "{colors.accent}"
    backgroundColor: "{colors.bg-elevated}"
  diff-added:
    textColor: "{colors.diff-add}"
    backgroundColor: "#0a160a"
  diff-removed:
    textColor: "{colors.diff-del}"
    backgroundColor: "#1a0a0a"
  diff-hunk-header:
    textColor: "{colors.diff-hunk}"
  diff-context-line:
    textColor: "{colors.diff-context}"
  plan-card:
    backgroundColor: "{colors.bg-elevated}"
    textColor: "{colors.text-primary}"
  overlay-popup:
    backgroundColor: "{colors.bg-elevated}"
    textColor: "{colors.text-primary}"
  command-palette:
    backgroundColor: "{colors.bg-elevated}"
    textColor: "{colors.text-primary}"
  muted-label:
    textColor: "{colors.text-muted}"
  # ── Agent avatars (TEAM mode) ──────────────────
  agent-avatar-orchestrator:
    textColor: "{colors.agent-orchestrator}"
  agent-avatar-researcher:
    textColor: "{colors.agent-researcher}"
  agent-avatar-planner:
    textColor: "{colors.agent-planner}"
  agent-avatar-executor:
    textColor: "{colors.agent-executor}"
  agent-avatar-reviewer:
    textColor: "{colors.agent-reviewer}"
  agent-avatar-tester:
    textColor: "{colors.agent-tester}"
  agent-avatar-debugger:
    textColor: "{colors.agent-debugger}"
  agent-avatar-assistant:
    textColor: "{colors.agent-assistant}"
  # ── Brand accent usage ─────────────────────────
  accent-mark:
    textColor: "{colors.accent-teal}"
---

## Overview

**Developer-dark terminal aesthetic.** A high-contrast, low-chroma palette designed
for extended coding sessions. The UI feels like a premium IDE theme — deep ink
backgrounds, soft foreground text, and a single blue accent that drives all
interaction affordances.

The design serves a multi-mode AI coding assistant running inside a terminal.
Four work modes (BUILD, PLAN, TEAM, GOAL) share a single chat view; modes are
distinguished by one color-coded tag in `AgentStatusBar`, not by separate panels or tabs.

**Core principle:** The chat stream is the only permanent surface. No sidebars,
no bottom panels. Every auxiliary function is an ephemeral overlay, dismissed
with `Esc`.

**Terminal constraint:** All colors are mapped to the ANSI 16-color palette of
Terminal.Gui v2. OKLCH hex values above represent design intent; the actual
rendering uses the closest named Terminal.Gui color (e.g. `accent #5b8dee` →
`BrightBlue`, `success #4caf84` → `BrightGreen`). No true-color or 256-color
fallback is assumed.

## Colors

The palette is divided into five functional groups.

### Backgrounds (4 levels)

- **bg-root (`#12151a`):** Terminal canvas — the deepest surface, used for the
  chat stream and input line.
- **bg-surface (`#181b22`):** Slightly elevated — agent status and session context bars.
- **bg-elevated (`#1e212a`):** Overlays, popups, diff blocks, plan cards,
  thinking panels. Distinguished from bg-root by its lighter tone.
- **bg-input (`#0f1218`):** Input field — the darkest surface, creating a
  recessed feel for the text entry area.

In 16-color mode, all four backgrounds collapse to `Black` / `DarkGray`.
Visual separation is achieved through borders (`DarkGray` vs `Cyan`) rather
than background contrast.

### Brand & Semantic (5 colors)

- **accent (`#5b8dee`):** The sole interaction driver in full-color contexts —
  links, active borders, PLAN mode, focused states. Never used for decoration.
  **Terminal mapping (normative):** interactive accent duty in the terminal is
  rendered by `accent-teal` via `TuiPalette.Accent`; `#5b8dee` appears only as
  the researcher role color (`TuiPalette.AgentBlue`).
- **accent-teal (`#14B8A6`):** Secondary brand color used as the product
  accent in terminal rendering (mapped to `BrightCyan`) — the single
  interaction driver color for TUI code (`TuiPalette.Accent`).
- **success (`#4caf84`):** BUILD mode identity, completed states, diff additions.
- **warning (`#e5b14c`):** Pending confirmations, tool-call indicators.
- **error (`#e0556a`):** Failures, rejected states, diff deletions.

### Text hierarchy (3 levels)

- **text-primary (`#d4d8e0`):** Body text, agent messages — the default foreground.
- **text-secondary (`#9298a4`):** Descriptions, status items, timestamps.
- **text-muted (`#5c6270`):** Separators, disabled states, placeholder text.

### Diff (4 tokens)

Standard unified-diff semantics: `diff-add` (green), `diff-del` (red),
`diff-hunk` (cyan for `@@ ... @@` headers), `diff-context` (gray for
unchanged lines).

### Agent 8-color system

Each AI agent role has a unique color for visual identification in TEAM mode's
multi-agent conversation stream:

| Role | Color | Hex | Semantic |
|------|-------|-----|----------|
| orchestrator | Purple | `#a386d8` | Leadership, coordination |
| researcher | Blue | `#5b8dee` | Investigation, analysis |
| planner | Green | `#4caf84` | Architecture, planning |
| executor | Orange | `#e08b5c` | Execution, building |
| reviewer | Yellow | `#e5b14c` | Code review, critique |
| tester | Red | `#e0556a` | Testing, validation |
| debugger | Pink | `#e07ba5` | Debugging, troubleshooting |
| assistant | Cyan | `#5bb8c8` | General assistance |

## Typography

All text is monospace. The type stack prioritizes programming fonts with
ligature support.

| Token | Font | Size | Weight | Line Height | Usage |
|-------|------|------|--------|-------------|-------|
| `heading` | JetBrains Mono | 15px | bold | — | Section headers in overlays |
| `body` | JetBrains Mono | 13px | normal | 1.55 | Chat messages, prose and input |
| `status-bar` | JetBrains Mono | 10.5px | normal | — | Status bar items |

Fallback chain: `JetBrains Mono → Cascadia Code → Fira Code → Consolas → monospace`.

In terminal rendering, font metrics are controlled by the terminal emulator,
not by the application. These tokens describe the **design intent**; actual
rendering uses the terminal's configured monospace font.

## Layout

### Component architecture

```
ReplShell
├── ChatTranscriptView
│   └── MessageListView
├── AgentStatusBar
├── ChatInputView
│   └── ChatTextEditor
├── SessionContextBar
└── OverlayHost
```

```text
│                                                       │
│       CHAT TRANSCRIPT — sole permanent surface        │
│                                                       │
├─ AgentStatusBar ─────────────────────────────────────┤
│ ⠋ 思考中 · Opus · 💰 $0.04 · 🔒 Sandbox · LSP: 2s  BUILD │
├─ ChatInputView ──────────────────────────────────────┤
│ _                                                     │
│                                                       │
├─ SessionContextBar ──────────────────────────────────┤
│ 📁 project · 🌿 main     轮次 5 · 12.5K↓ 8.3K↑ · ctx 45% │
└──────────────────────────────────────────────────────┘
```

The visible components have explicit, non-overlapping responsibilities:
**ChatTranscriptView** owns the current conversation transcript and streaming
lifecycle, while **MessageListView** owns rendered rows, scrolling, search,
copy, expansion, and reflow. **AgentStatusBar** is the single source of agent
runtime and orientation state (activity, model, cost, sandbox, LSP, working
mode, TEAM strategy/team). **ChatInputView** owns the separator, chat-input
state, multiline editing, completion, paste handling, mode-shortcut bridge,
and dynamic height; its **ChatTextEditor** child is the sole input focus target.
**SessionContextBar** shows workspace identity and session consumption metrics.
**OverlayHost** owns temporary modal and side-cover surfaces.

### Structural rules

1. **Chat is the only permanent content surface.** Auxiliary views are overlays
   managed by `OverlayHost` and dismissed with `Esc`.
2. **No independent top header.** Agent activity and working mode live together
   in `AgentStatusBar`; do not recreate `TuiTitleBar` or `WorkspaceHeader`.
3. **One input owner.** `ChatInputView` directly owns `ChatTextEditor`; do not add
   a wrapper bar solely for layout or mode-shortcut forwarding.
4. **Dynamic input height.** `ChatInputView` is 4 lines at minimum and 5 lines at
   maximum: one separator plus 3–4 visible editor lines. The content zone reserves
   the maximum height to prevent overlap.
5. **SessionContextBar is always visible.** Left side shows workspace/git/session
   identity; right side shows turn, token and context-window metrics. Segments are
   conditional to avoid startup clutter.
6. **Spacing unit is 4px** in design mockups. In terminal cells, horizontal
   padding is expressed as character-width gaps (typically 1–2 cells).

### Overlay system

All overlays are managed by a central `OverlayHost`. They stack above the
chat view and follow a uniform lifecycle:

- **Open:** keyboard shortcut or command
- **Close:** `Esc` (universal), or auto-close on selection
- **Types:** centered popup (Settings, Resume, Review) or side cover (Diff detail)

| Overlay | Trigger | Type |
|---------|---------|------|
| Review mode | `/diff` | centered |
| Settings | `/config` | centered |
| Resume chooser | `/session` | centered |
| Diff detail | Enter on Review file | side panel |

Slash commands are discovered via `/` completion — there is no separate command-palette shortcut.

## Elevation & Depth

Terminal UIs cannot render box-shadows. Elevation is communicated through
**background contrast** and **border emphasis** alone:

| Level | Surface | Background | Border | Usage |
|-------|---------|------------|--------|-------|
| 0 — Root | Chat view | bg-root | none | Default surface |
| 1 — Raised | Agent/Session status bars | bg-surface | DarkGray | Slightly elevated bars |
| 2 — Elevated | Popup / Card | bg-elevated | DarkGray | Overlays, diff blocks, plan cards |
| 3 — Active | Focused overlay | bg-elevated | Cyan (border-active) | Currently focused overlay |

The transition from level 0 → 2 is a noticeable lightening of the background,
signaling that the overlay sits "above" the chat. An active overlay further
distinguishes itself with a cyan border.

## Shapes

Terminal UIs have no border-radius. All elements are rectangular, drawn with
box-drawing characters (`─ │ ┌ ┐ └ ┘`). Visual "softness" is achieved through
character choice:

- **Standard borders:** Single-line box-drawing (`─ │ ┌ ┐ └ ┘`)
- **Emphasis:** Double-line or heavy borders for focused/active elements
- **Separators:** Thin horizontal rules (`─`) between logical sections
- **Vertical indicators:** Left-border color bars (e.g., thinking block uses a
  cyan left-border to mark AI reasoning content)

The only "shape variation" in the system is the **colored dot** (`●`) used
for status indicators (agent active, strategy type) and the **colored bar**
(`▎`) used as a mode indicator prefix on user messages.

## Components

### Mode tag

A colored badge displayed on the right side of `AgentStatusBar`, identifying the
current work mode. This is the single persistent visual differentiator between modes.

| Variant | Background | Text | Border |
|---------|-----------|------|--------|
| BUILD | `success` | bg-root | none |
| PLAN | `accent` | bg-root | none |
| TEAM | `mode-team` | bg-root | none |
| GOAL | `mode-goal` | bg-root | none |

### Message blocks

Each message in the chat stream is a self-contained block:

- **User message:** Cyan `▎` left-bar + content + right-aligned timestamp
- **Assistant message:** Agent-colored avatar + name + markdown body
- **Thinking block:** Cyan left-bar + bg-elevated background + gray analysis
  text with breathing-dot animation while streaming
- **Tool call row:** Orange `⚡` icon + tool name + truncated args + green `✓`
  on completion
- **Diff block:** Unified diff format with green/red line backgrounds

### Plan card (PLAN mode)

An interactive card embedded in the chat stream. The agent's first response
in PLAN mode always contains a plan card.

- Background: bg-elevated
- Border: DarkGray (default) → Cyan (focused)
- Steps: numbered list, each tagged with an assigned agent color
- PendingApproval 阶段由对话流内 InlineSelector 决策面板提供
  批准 / 输入修改意见 / 拒绝 选择（↑↓ + Enter 确认、Esc 取消）

After approval, steps are progressively marked as completed (green check).

### Agent coordination message (TEAM mode)

A compact message showing inter-agent delegation:

```
orchestrator → researcher    Investigate Terminal.Gui constraints
```

- Left side: sender agent color
- Arrow: text-muted
- Right side: receiver agent color + task description

### Agent status bar

A single-line bar showing **agent runtime state and orientation**:

```
⠋ 思考中 · Opus · 💰 $0.04 · 🔒 Sandbox · LSP: 2s       BUILD
```

- Left group: animated activity indicator while busy, activity phase, model,
  running cost, sandbox and conditional LSP diagnostics
- Right group: current mode badge, plus TEAM strategy and active team when applicable
- Idle state omits the spinner/activity label instead of showing a decorative dot
- Text color: text-secondary; separators use muted `·`

The activity indicator is driven by `SpinnerController` so timing and repaint
logic remain outside the rendering method.

### Session context bar

A single-line `SessionContextBar` at the very bottom showing **workspace identity
and session consumption metrics**:

```
📁 project  🌿 main  · 轮次 5  · 1.2K↓ 800↑  · ctx 200K [██████░░░░] 45%
```

Segments are conditionally rendered (only when non-zero / non-default):

| Segment | Icon | Condition | Color |
|---------|------|-----------|-------|
| Workspace | 📁 | always | fg-primary |
| Git branch | 🌿 | git available | accent |
| Worktree | 📦 | inside linked worktree | info |
| Turn number | — | `turn > 0` | fg-secondary |
| Token usage | ↓↑ | `tokens > 0` | fg-secondary |
| Context window | ctx | `maxContext > 0` | max-context value fg-secondary; bar/percent ratio-colored (green/amber/red) |

Context-window ratio uses a three-tier color scheme: green (<50%),
amber (50–80%), red (≥80%) to give early warning before the context
window fills.

### Empty state (welcome screen)

When the conversation has no messages (startup or after conversation reset), the
chat view renders a centered welcome screen instead of being blank:

```



          █████   ████  ██████  █████
          █       █  █  █    █  █
          █       █  █  █    █  ███
          █       █  █  █    █  █
          █████   ████  ██████  █████

                  v1.0.0

    / 斜杠命令 · @ 提及文件 · Tab 空输入切模式 · Esc 中断 · /find 搜索

```

This is **not** a separate view — it is rendered inline by
`ChatTranscriptView` via `WelcomeRenderer` as the first block in the
`MessageListView`. Once the user sends a message or the agent responds,
the welcome screen is replaced by the transcript content.
The welcome screen re-renders on terminal resize to stay centered.

## Do's and Don'ts

### Do's

- **Use the palette constants for all colors.** Never hard-code named
  Terminal.Gui colors or hex values in component code — always reference
  the palette's semantic tokens (e.g. `Accent`, `Success`, `AgentPurple`).
- **Use semantic color names.** Reference colors by their role (`success`,
  `error`, `accent`), not by their Terminal.Gui mapping (`BrightGreen`,
  `BrightRed`, `BrightBlue`).
- **Keep the chat view sacred.** All auxiliary UI must be an overlay that
  can be dismissed with `Esc`. Never add persistent panels to the chat area.
- **Indicate mode once.** Render the current mode and TEAM strategy/team only in
  `AgentStatusBar`. `ChatInputView` handles mode shortcuts but does not duplicate
  mode labels. A single **transient** mode-change banner in the transcript (rendered
  once on switch and replaced in place, never accumulating) is permitted as change
  feedback; it must never become a persistent per-message label.
- **Advertise only real interactions.** Welcome-screen tips and inline hints must
  match actual key behavior (e.g. Esc interrupts/cancels; it does not clear input).
- **Use the agent 8-color system consistently.** When rendering any
  agent-identified content (messages, plan steps, coordination lines),
  color-code it with `TuiPalette.FromAgentName()`.
- **Animate thinking states.** Use the breathing-dot animation for
  in-progress thinking blocks. Use spinner animation for tool calls in
  progress.
- **Respect overlay stacking.** Multiple overlays may stack. Always use
  `OverlayHost` to manage z-order and `Esc` propagation.

### Don'ts

- **Don't use pure black `#000000` for backgrounds.** Use `bg-root` (`#12151a`)
  — it's warmer and reduces eye strain.
- **Don't add box-shadows or gradients.** Terminal UIs cannot render them.
  Use background contrast and border emphasis for depth.
- **Don't use more than 3 font sizes.** The system defines heading, body and
  status-bar sizes. Prefer `body` for content and input.
- **Don't hard-code agent colors.** Always resolve agent colors through the
  8-color agent system defined above. Unknown agents fall back to
  `agent-orchestrator` (purple).
- **Don't split agent state across multiple bars.** Activity, model, cost,
  sandbox, LSP and working-mode orientation belong to `AgentStatusBar`.
  Workspace and context-window metrics belong to `SessionContextBar`.
- **Don't use the accent color for decoration.** It is reserved exclusively
  for interactive affordances (links, focused borders, PLAN mode tag).
  Overuse dilutes its signal.
- **Don't render thinking text in primary color.** Thinking blocks should
  use `text-secondary` or `text-muted` to visually de-emphasize AI reasoning
  relative to final responses.

