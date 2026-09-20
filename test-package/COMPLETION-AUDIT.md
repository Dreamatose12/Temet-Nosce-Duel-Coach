# Duel practice completion audit

Status is intentionally separated into implementation, offline verification, and native/live verification.

| Requirement | Current evidence | Status |
|---|---|---|
| Strict practice state machine | `PracticeService.cs`, explicit `PracticeState` | Implemented; native event timing pending |
| Private commands and denial reasons | tell dispatcher plus 45 C# checks | Offline verified; two-account pending |
| Punchbag policy | target/action gates and timer | Offline verified; native no-action proof pending |
| Worthy policy | health hysteresis plus raw-packet ceiling | Offline verified; native damage calibration pending |
| AI Overlord | burst controls, reactive perk gate, adaptive gear | Build/offline verified; native combat pending |
| Readiness gate | health/nano, locks, perks, specials, pending actions, gear, zoning, settling | Offline verified; native SDK values pending |
| Decision/execution separation | `DecisionLayer.cs` plus executor hooks | Offline/build verified; native action timing pending |
| Evidence-aware monitoring | quality labels, unverified target stats, gaps, unknown outcome | 24 Python tests pass |
| Transactional equipment | dry-run, storage policy, request/result/restore gates | Build/offline verified; successful/failed native swaps pending |
| Scoped TestPlugin diagnostics | damage, character action, item-use observations | Build verified; packet semantics pending |
| Duel report schema | JSONL start/state/sample/action/equipment/packet/outcome/end events | Offline analyzer verified; native complete report pending |
| Discord report delivery | `Coach/discord_bridge.py --watch` | Code/test verified; channel credentials and delivery pending |
| Discord coaching | `!coach`, 15-second throttle, advice-only Responses client, required sections | Code/test verified; real API/Discord flow pending |
| Repeated native stability | No AO client was running during offline work | Pending two-account runtime test |

Do not mark the project complete until the pending native/live rows have direct evidence. The LLM never chooses combat actions, equipment names, duel acceptance, or plugin settings.
