namespace OneCode.Core.Skills;

/// <summary>Represents a skill loaded from a bundled markdown file.</summary>
public sealed record BundledSkill(
    string Name,
    string Description,
    string Prompt);

/// <summary>
/// Provides access to built-in bundled skills.
/// These are the core operational skills shipped with OneCode.
/// </summary>
public static class BundledSkills
{
    private static readonly Lazy<Dictionary<string, BundledSkill>> _skills = new(LoadBundledSkills);

    public static IReadOnlyDictionary<string, BundledSkill> All => _skills.Value;

    public static BundledSkill? Get(string name) => _skills.Value.TryGetValue(name, out var skill) ? skill : null;

    private static Dictionary<string, BundledSkill> LoadBundledSkills()
    {
        var skills = new Dictionary<string, BundledSkill>(StringComparer.OrdinalIgnoreCase);

        RegisterSkill(skills, CreateBatchSkill());
        RegisterSkill(skills, CreateDebugSkill());
        RegisterSkill(skills, CreateLoopSkill());
        RegisterSkill(skills, CreateStuckSkill());
        RegisterSkill(skills, CreateVerifySkill());
        RegisterSkill(skills, CreateSimplifySkill());
        RegisterSkill(skills, CreateSkillifySkill());
        RegisterSkill(skills, CreateRememberSkill());
        RegisterSkill(skills, CreateVerifyContentSkill());

        return skills;
    }

    private static void RegisterSkill(Dictionary<string, BundledSkill> dict, BundledSkill skill)
        => dict[skill.Name] = skill;

    private static BundledSkill CreateBatchSkill() => new(
        Name: "batch",
        Description: "Orchestrate parallel work across multiple agents with git worktrees",
        Prompt: @"# Batch: Parallel Work Orchestration

You are orchestrating a large, parallelizable change across this codebase.

## User Instruction

{instruction}

## Phase 1: Research and Plan (Plan Mode)

1. **Understand the scope.** Research what this instruction touches.
2. **Decompose into independent units.** Break into 5-30 self-contained units, each implementable in an isolated git worktree.
3. **Determine the e2e test recipe.** Figure out how a worker can verify its change end-to-end.

## Phase 2: Create Worktrees

Create a worktree for each unit: `git worktree add ../worktree-<N> -b batch-<N>`

## Phase 3: Dispatch Workers

Launch worker agents, each in its own worktree, with its specific unit of work.

## Phase 4: Collect and Merge

Once all workers finish, merge their worktrees back into main.");

    private static BundledSkill CreateDebugSkill() => new(
        Name: "debug",
        Description: "Systematic debugging methodology for investigating issues",
        Prompt: @"# Debug: Systematic Debugging Methodology

You are a systematic debugger. Your job is to investigate, isolate, and resolve issues.

## Methodology

1. **Reproduce the issue.** First confirm the bug exists.
2. **Isolate the cause.** Narrow down to the smallest possible reproduction.
3. **Hypothesize.** Form a theory about the root cause.
4. **Test the hypothesis.** Verify by making a minimal change.
5. **Fix the issue.** Implement the minimal fix.
6. **Verify the fix.** Confirm the issue is resolved and no regressions.

## User Issue

{issue}");

    private static BundledSkill CreateLoopSkill() => new(
        Name: "loop",
        Description: "Execute a task repeatedly until the result matches the target",
        Prompt: @"# Loop: Iterative Execution Until Correct

You will execute a task repeatedly until the output matches what is expected.

## Instructions

1. Execute the task.
2. Compare the result against the expected output.
3. If incorrect, analyze what went wrong, fix it, and retry.
4. Repeat until correct or maximum iterations reached.

## Task

{task}

## Expected Result

{expected}");

    private static BundledSkill CreateStuckSkill() => new(
        Name: "stuck",
        Description: "What to do when you're stuck in a loop or can't make progress",
        Prompt: @"# Stuck: Recovery from Unproductive State

You appear to be stuck — repeating the same actions without progress.

## Recovery Steps

1. **Pause.** Stop what you're doing.
2. **Assess.** What have you tried? What worked and what didn't?
3. **Change approach.** Try a fundamentally different strategy.
4. **Ask for help.** If still stuck, use AskUserQuestion to get guidance.

## Current State

{context}");

    private static BundledSkill CreateVerifySkill() => new(
        Name: "verify",
        Description: "Verify that changes are correct and complete",
        Prompt: @"# Verify: Change Verification and Validation

You need to verify that recent changes are correct and complete.

## Verification Checklist

1. **Build check.** Does the project build cleanly?
2. **Test check.** Do all tests pass?
3. **Lint check.** Are there any linting warnings?
4. **Manual check.** Does the feature work as expected?
5. **Edge cases.** Are edge cases handled?

## Project Context

{context}");

    private static BundledSkill CreateSimplifySkill() => new(
        Name: "simplify",
        Description: "Review and simplify code after making changes",
        Prompt: @"# Simplify: Post-Implementation Review and Cleanup

After implementing changes, review and simplify.

## Review Steps

1. **Read all changes.** Understand every line changed.
2. **Look for duplication.** Remove redundant code.
3. **Simplify logic.** Replace complex logic with simpler alternatives.
4. **Check conventions.** Ensure code follows project conventions.
5. **Remove dead code.** Delete unused imports, variables, functions.

## Current Changes

{changes}");

    private static BundledSkill CreateSkillifySkill() => new(
        Name: "skillify",
        Description: "Capture this conversation as a reusable skill",
        Prompt: @"# Skillify: Create a Reusable Skill

Analyze this conversation and create a reusable skill file.

## Instructions

1. Review the conversation history to understand the repeated pattern or workflow.
2. Write a new skill file at `.onecode/skills/<skill-name>.md`.
3. The skill file should include:
   - A YAML frontmatter block with `name`, `description`, and `argument-hint` fields.
   - Clear, actionable instructions in the body that can be executed by an AI agent.
   - `$ARGUMENTS` placeholder where the user's specific input should be substituted.
4. Make the skill general enough to be reused but specific enough to be useful.
5. Confirm the skill file path and summarize what it does.

## Output

After writing the file, show the full skill content and explain how to invoke it.");

    private static BundledSkill CreateRememberSkill() => new(
        Name: "remember",
        Description:
            "Write a lasting project rule into AGENTS.md (not MEMORY.md — use /memory for searchable facts)",
        Prompt: @"# Remember: Update AGENTS.md Project Rules

Write the following into `AGENTS.md` as a **project coding/process rule** that agents and humans should follow in this repository.

This is **not** the searchable memory subsystem. Do **not** edit `MEMORY.md` or call memory tools.
- `/remember` → `AGENTS.md` (project conventions, injected as Project Context)
- `/memory add` → `MEMORY.md` (searchable facts/preferences; use that for ephemeral recall instead)

## What to Persist

$ARGUMENTS

## Instructions

1. Read the existing `AGENTS.md` in the project root (create it if it doesn't exist).
2. Add the information in an appropriate section (Build, Testing, Conventions, etc.). Use a `## Agent Notes` section if no obvious section fits — prefer that over a generic `## Memory` heading so it is not confused with MEMORY.md.
3. Keep entries concise — one to three sentences each. Phrase as durable rules (must / never / prefer), not as chat notes.
4. Do not duplicate existing entries.
5. Write the updated file back.
6. Confirm what was saved, and mention that searchable facts belong in `/memory add` instead.");

    private static BundledSkill CreateVerifyContentSkill() => new(
        Name: "verify-content",
        Description: "Review generated content for accuracy, completeness, and quality",
        Prompt: @"# Verify Content: Quality and Accuracy Review

Review the following content for accuracy, completeness, and quality.

## Content to Verify

$ARGUMENTS

## Verification Checklist

1. **Factual accuracy** — Are all claims correct? Flag any that seem incorrect or unverifiable.
2. **Completeness** — Does the content address the original requirement fully?
3. **Clarity** — Is the content clear and unambiguous?
4. **Consistency** — Are terminology and style consistent throughout?
5. **Code correctness** (if applicable) — Does any code compile and run as described?
6. **Security** (if applicable) — Are there any obvious security issues?

## Output

- List each issue found with its severity: Critical / Warning / Suggestion.
- Provide a corrected version if significant issues are found.
- End with an overall assessment: Pass / Pass with minor issues / Fail.");
}
