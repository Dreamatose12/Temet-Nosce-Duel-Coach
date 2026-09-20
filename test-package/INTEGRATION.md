# Duel practice integration (in progress)

This is source/build work, not a verified live deployment. Do not load this alongside another Agent handler, a standalone EquipManager, or the original TestPlugin: they can compete for commands, actions, or duel callbacks.

## Practice

Tell the character `help` or `!help`, then `practice 1`, `practice 2`, or `practice 3`. The bot reserves the participant, checks readiness again, and sends the challenge after 15 seconds. Direct incoming challenges are declined. `status` reports readiness; `cancel` is participant-only; `report` and `coach <question>` are private post-duel instructions for the last participant.

Punchbag blocks retaliation for five minutes after the countdown. Worthy Opponent uses a paced combat policy with a health-based recovery pause and a conservative raw-packet damage ceiling over a ten-second window; packet semantics remain explicitly unverified. It is not yet an adaptive learning model. Overlord enables the burst sequencer and holds opponent-targeted perks while Dance of Fools is observed. After the opening sequence, normal managed actions are no longer suppressed solely because Burst Control is enabled.

## Embedded EquipManager

`EquipmentEngine.cs` and `UI/EquipManagerWindow.xml` originate from the user's `New project 2/AOSharpPluginTemplate` EquipManager source. The engine is embedded, not a second plugin entry point. The practice handler drives updates and exposes the existing `/equip` commands and UI. Displaced items still require remembered source bags or an explicitly configured overflow bag; there is no inventory fallback.

The character's presets remain in `EquipManagerConfig.json` beside the plugin. No existing character configuration has been imported automatically. Use `/equip help`, `/equip ui`, and `/equip dryrun <preset>` to configure and inspect the character's actual equipment. Open relevant inventory bags before starting automatic practice swaps.

`DuelAdaptiveEquipment.json` is created on initialization with automation disabled. Configure these fields before enabling it and reload the plugin:

```json
{
  "Enabled": false,
  "AttackPreset": "maxar",
  "NanoDefensePreset": "maxnr",
  "LowHealthPreset": "defense",
  "ReturnPreset": "baseline",
  "LowHealthPercent": 35,
  "MinimumSwapIntervalSeconds": 10
}
```

These are preset names, not prebuilt gear sets. Every adaptive preset must exist, and the return preset must cover every adaptive slot and be observed equipped before practice. The engine respects slot locks and missing-item blockers. Failed or partially observed swaps suspend further adaptation for that duel. Following a gear change, rematches remain blocked until the return preset is observed equipped. If restoration fails, resolve storage/missing gear through `/equip`; do not force another duel. Unloading cannot guarantee restoration because no further game updates can run.

Selection priority is low local health, observed Dance of Fools (configured attack gear), caster-profession heuristic (configured nano defense), otherwise configured attack gear. These are explicit heuristics, not claims that raw target stats or gear are trustworthy. Automated swaps are Overlord-only and rate-limited. Requests and observed results are separately recorded.

## Recording and unfinished work

`DuelReports/*.jsonl` records samples, incremental raw target stats, scoped TestPlugin damage and character-action packet observations, duel statuses, decisions, and equipment requests/results. Raw target stats are marked unverified; client data does not establish full server-side statistics. An outcome is marked only when exactly one participant's client alive state is observed false; ordinary stops remain `unknown`. The original TestPlugin's unconditional duel acceptance and arbitrary developer commands are not imported.

Still pending: broader useful TestPlugin diagnostics, evidence-backed duel analysis and coaching, Discord/LLM service, full defect-first review, packaged deployment, and native two-account tests. Discord server: Temet Nosce; report channel and Q&A location remain unspecified. No Discord messages or LLM API requests have been sent.

Build success does not prove native stat-read safety, packet actor interpretation, cooldown readiness, gear swapping, or duel outcomes. Current deterministic tests use a fake SDK and fake equipment engine to exercise service transitions and adaptive policy; they do not exercise actual inventory moves or the AO client.
