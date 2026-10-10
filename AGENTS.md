# Working on BatCave

Read [README.md](README.md), [CONTRIBUTING.md](CONTRIBUTING.md), and
[SECURITY.md](SECURITY.md) before editing. These instructions apply throughout
this repository unless a closer AGENTS.md adds requirements.

## Coordinate the change

Work on a focused branch and prepare a pull request. Read existing platform
work before changing shared behavior. Keep native code in its platform directory
and shared contracts in `Universal/`. Coordinate ownership when another contributor
is editing the same files. Do not overwrite their work or replace an installed
working app during development.

The app opens directly to nearby computers. Permissions, screen arrangement and
sharing controls are optional navigation, not a mandatory numbered wizard. Keep
the normal flow identical on Windows, Mac and Linux: select a nearby computer,
compare the code, then explicitly link and share on both. Address discovery and
default arrangement are automatic. Present one main action at a time and use
BatCave as the product name. Avoid demo or trial wording. Explain the device action
in plain language; put development details in technical docs.

## Preserve the trust boundary

Keep explicit device approval, pinned mutual TLS 1.3, bounded messages, and held-input
cleanup. Navigation, saved setup, and reconnect must not silently authorize input.
Unavailable features remain disabled until their implementation and relevant
checks pass. Update both sides of a contract before enabling an incompatible wire
change.

Never commit private device state or unredacted local logs. Use synthetic data in
screenshots and tests. The maintainer name and X contacts in README/SECURITY are
intentionally public; that does not authorize publishing machine-specific details.
Preserve identity/profile identifiers during branding changes so upgrades do not
discard trust or overwrite another installation.

## Validate and report

Run the relevant CONTRIBUTING.md checks, the source privacy gate, and
`git diff --check`. Inspect GUI changes at default and minimum sizes. Report what
actually ran and which physical-device checks remain. Passing a fixture does not
establish a real desktop permission flow or cross-platform input session.
Keep release notes consistent with that evidence.

Use direct, concise prose. Avoid catalogue prose and list-stacking; prefer one
clear claim when several items express the same idea.
