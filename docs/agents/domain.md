# Domain docs

## Layout and reading rules

This repository uses a single-context layout:

- `CONTEXT.md` at the repository root holds domain terminology.
- `docs/adr/` holds architecture decision records.

Before exploring the codebase, read `CONTEXT.md` and the ADRs relevant to the area of work. If either is absent, proceed silently. The `domain-modeling` skill creates these documents when terminology or decisions are resolved.

## Vocabulary

Use the terms defined in `CONTEXT.md` when naming domain concepts in issues, proposals, hypotheses, and tests. If a needed term is missing, check existing project language before noting a glossary gap for `domain-modeling`.

## Decision conflicts

If a proposal contradicts an existing ADR, identify that ADR and explain why the decision should be reconsidered before proceeding with the conflicting change.
