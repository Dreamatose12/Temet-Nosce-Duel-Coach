# Native two-account test checklist

This checklist is the remaining verification gate. Use two AO accounts, the bot account and a learner account, at the same location. Load only `ProfessionHandler.Agent.DuelPractice.dll` from the current package; do not load the standalone EquipManager or original TestPlugin beside it.

For each test, save the generated `DuelReports/<session>.jsonl` and note whether the observed result matches the expected result. A successful build or a generated file is not runtime proof.

## Command and readiness flow

1. Learner tells `help` and `!help`; both return the same menu.
2. Learner tells `status`; an explanation is returned.
3. Learner tells `practice 1`, then immediately tells `practice 2`; mode 1 remains reserved.
4. While the bot has a skill lock, pending cast, unavailable perk, unavailable special, low health/nano, or an equipment swap, request a practice mode. Each request is denied with a reason.
5. Clear the condition. Confirm the bot challenges only after the delay and the second readiness check.
6. Send duplicate duel status-accepted events if the test harness/client can reproduce them. The session timer must not reset.
7. Test `cancel` from the learner and a bystander. Only the learner cancels the session.

## Modes

- Punchbag: confirm no attack, perk, item debuff, hostile spell, special, or pet attack is sent. Confirm the five-minute timer starts after countdown and ends cleanly.
- Worthy Opponent: drive the learner below the recovery threshold, confirm the bot stops attacking, then heal the learner above the resume threshold and confirm combat resumes without a kill-combo sequence.
- AI Overlord: confirm configured burst actions execute, Dance of Fools causes opponent-targeted perks to be held only while the buff is observed, self-defense remains allowed, and adaptive gear requests are rate-limited.

## Cleanup and reports

Repeat a session while zoning, dying, disconnecting the learner, and timing out. Confirm duel stop, attack/pet cleanup, recorder end record, and recovery gate. A normal stop or timeout must produce `outcome: unknown`; exactly one observed dead participant may produce an explicit outcome.

Confirm every report contains state transitions, readiness blocks, health/nano samples, buff durations, action requests, item-use events, damage/character-action packet observations, equipment request/result events, decisions, and an end record. Review raw target stats as unverified client observations.

## Sidecar

Set credentials only in the sidecar process environment. Run `discord_bridge.py <DuelReports> --watch --poll` and confirm one report is published to the configured Temet Nosce channel. Ask `!coach what should I practice next?` and confirm the answer has the five required headings and does not issue a game command. Repeat a question rapidly to confirm the sidecar/API rate limiting and error handling are acceptable.
