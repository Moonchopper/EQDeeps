# Contributing to EQDeeps

Bug reports, log lines the parser gets wrong, and pull requests are all welcome.
The most valuable thing anyone can send is **a log line EQDeeps parses badly**,
with what it should have meant — that is the one input nobody can synthesize.

## Licensing of contributions

EQDeeps is [MIT](LICENSE), and contributions come in under the same terms:
what you send is offered under the MIT Licence, and you keep your copyright in
it. Nothing here asks you to assign anything or to sign a contributor licence
agreement.

Instead, certify what you send with the [Developer Certificate of
Origin](https://developercertificate.org/) — the same one the Linux kernel
uses. It is three short paragraphs that say you wrote the patch, or have the
right to submit it. You certify it by adding a line to each commit:

    Signed-off-by: Your Name <your.email@example.com>

`git commit -s` writes that line for you from your git identity, and it can be
combined with anything else you pass:

```bash
git commit -s -m "Fix the frenzy grammar"
git commit -s -F message.txt          # long messages live in a file, see below
```

A workflow checks every commit in a pull request for the line. If you forget,
`git rebase --signoff main` fixes a branch in one go.

Two things follow from this that are worth being explicit about:

- The name and icon are not part of the MIT grant. See [TRADEMARKS.md](TRADEMARKS.md).
- Because contributions stay under their authors' copyright, the project's
  licence cannot be changed later without the agreement of everyone who has
  contributed. That is deliberate.

## Before you write code

- **Read [CLAUDE.md](CLAUDE.md).** It is the orientation document — repo map,
  the invariants that must not break, which doc answers which question. It
  exists so you don't have to re-derive the project from the source.
- **`docs/` is the spec of record.** If reality disagrees with a domain doc, the
  doc gets fixed in the same change.
- **EQDeeps is a clean-room implementation.** [EQLogParser][eqlp] is the
  incumbent and is worth reading to settle a question about *behaviour* — what
  a log line means, how a formula ought to work. Write the answer into the
  domain doc and implement it fresh. **Do not port or transcribe its code.**

[eqlp]: https://github.com/kauffman12/EQLogParser

## Working on it

```powershell
npm --prefix ui install          # first time only
npm --prefix ui run build        # the server serves the built SPA
dotnet run --project src/EQDeeps.Server
dotnet test
```

The full command set, the environment traps that will bite you, and the flag
list are in [CLAUDE.md §3](CLAUDE.md).

- **Branch, then PR.** `feat/…`, `fix/…`, `chore/…`, `docs/…`. Work reaches
  `main` through a pull request with CI green.
- **Adding a grammar means adding fixtures.** `tests/EQDeeps.Core.Tests/Fixtures/*.json`
  is the parser corpus, and fidelity against it is a release gate.
- **Query-engine tests check hand-computed values,** not the engine's own
  output. A golden file recorded from the code under test cannot catch a
  formula drifting, which is the entire point of those tests.
- **Warnings are errors.** `TreatWarningsAsErrors` is on; fix a warning rather
  than suppressing it, or leave a comment saying why it had to be suppressed.
- **Every dependency must be MIT / Apache-2.0 / BSD.** If it ships, it goes in
  [NOTICE](NOTICE) *and* its licence text goes in
  [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). Attribution is not the
  obligation — the licence text travelling with the binary is.

## Commits and pull requests

Commit subjects are behavioural sentences, not changelog fragments — what
changed for the person using the app, in plain language:

> Stop a missed frenzy from inventing a fight called "On a spite golem"

Bodies are prose: the problem, the reasoning, the numbers behind it, and what
was deliberately *not* solved. Long messages are easier to write to a file and
pass with `git commit -s -F message.txt` than to wrestle through a shell.

If the change is one an app user would notice, add a bullet under `## Unreleased`
in [CHANGELOG.md](CHANGELOG.md), written for them rather than for us.

## Reporting a parsing bug

Include the log line verbatim, what EQDeeps made of it, and what it should have
meant. Server and expansion help, since grammars differ between live EverQuest
and EverQuest Legends. Please scrub anything you would not want public —
character names in the line are usually the point, so say so if they matter.

## What is out of scope

`docs/product/vision.md` lists the non-goals, and they are non-goals on
purpose: no overlays, no triggers, no audio, nothing that talks to the game
client, no cloud, no telemetry. EQDeeps reads a text file and nothing else.
