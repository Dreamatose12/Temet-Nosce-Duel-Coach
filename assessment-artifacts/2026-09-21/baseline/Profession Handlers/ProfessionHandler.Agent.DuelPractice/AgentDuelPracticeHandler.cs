using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AOSharp.Common.GameData;
using AOSharp.Common.GameData.UI;
using AOSharp.Core;
using AOSharp.Core.UI;
using ProfessionHandler.Agent;
using ProfessionHandler.Generic;

namespace ProfessionHandler.Agent.DuelPractice
{
    public partial class AgentDuelPracticeHandler : AgentProfessionHandler
    {
        private const double DuelCountdownSeconds = 5.1;
        private const double DuelAcceptanceDelaySeconds = 15.0;
        private const double AttackRetryIntervalSeconds = 0.5;
        private const int AbsoluteConcentrationSpellId = 160710;
        private const int BreakOutSpellId = 227778;

        private enum PreDuelActionKind
        {
            MyOwnFortress,
            BreakOut,
            Recalibrate,
            AbsoluteConcentration
        }

        private sealed class PreDuelActionState
        {
            public PreDuelActionKind Kind { get; }
            public string Name { get; }
            public int TriggerAtMs { get; }
            public bool Complete { get; set; }

            public PreDuelActionState(PreDuelActionKind kind, string name, int triggerAtMs)
            {
                Kind = kind;
                Name = name;
                TriggerAtMs = triggerAtMs;
            }
        }

        private sealed class DuelRecord
        {
            public int OpponentId { get; set; }
            public string OpponentName { get; set; }
            public int BotWins { get; set; }
            public int OpponentWins { get; set; }
        }

        private Identity _opponent = Identity.None;
        private DuelRequestEventArgs _pendingDuelRequest;
        private Identity _pendingChallenger = Identity.None;
        private string _pendingChallengerName;
        private double _pendingDuelAcceptAt;
        private int _lastAcceptanceSecondsAnnounced;
        private double _attackReadyAt;
        private double _nextAttackAttempt;
        private readonly List<PreDuelActionState> _preDuelActions = new List<PreDuelActionState>();
        private double _preDuelStartedAt;
        private bool _preDuelActive;

        private readonly BurstSequenceWindowController _burstWindow;
        private List<BurstSequenceStep> _activeBurstSteps = new List<BurstSequenceStep>();
        private int _activeBurstStepIndex;
        private double _nextBurstStepAt;
        private double _burstStepDeadline;
        private bool _burstRunning;
        private bool _autoBurstArmed;

        private readonly Dictionary<int, DuelRecord> _duelRecords = new Dictionary<int, DuelRecord>();
        private readonly string _duelRecordPath;
        private string _activeOpponentName;
        private int _minimumLocalDuelHealth = int.MaxValue;
        private int _minimumOpponentDuelHealth = int.MaxValue;
        private bool _duelOutcomeRecorded;

        public AgentDuelPracticeHandler(string pluginDir) : base(pluginDir)
        {
            _duelRecordPath = Path.Combine(pluginDir, "DuelPracticeRecords.tsv");
            _reportDirectory = Path.Combine(pluginDir, "DuelReports");
            LoadDuelRecords();

            _settings.AddVariable("DuelPracticeEnabled", false);
            _settings.AddVariable("DuelBurstControlEnabled", false);
            _settings.AddVariable("DuelBurstStepTimeoutMs", BurstSequenceSettings.DefaultStepTimeoutMs);
            _settings.AddVariable("DuelBurstTopLeftX", 50f);
            _settings.AddVariable("DuelBurstTopLeftY", 50f);
            _settings.AddVariable("DuelBurstWidth", 470f);
            _settings.AddVariable("DuelBurstHeight", 520f);
            _settings.AddVariable(BurstSequenceSettings.PreMyOwnFortressEnabled, true);
            _settings.AddVariable(BurstSequenceSettings.PreMyOwnFortressAtMs, 0);
            _settings.AddVariable(BurstSequenceSettings.PreBreakOutEnabled, true);
            _settings.AddVariable(BurstSequenceSettings.PreBreakOutAtMs, 250);
            _settings.AddVariable(BurstSequenceSettings.PreRecalibrateEnabled, true);
            _settings.AddVariable(BurstSequenceSettings.PreRecalibrateAtMs, 500);
            _settings.AddVariable(BurstSequenceSettings.PreAbsoluteConcentrationEnabled, true);
            _settings.AddVariable(BurstSequenceSettings.PreAbsoluteConcentrationAtMs, 4300);

            for (var index = 0; index < BurstSequenceSettings.SlotCount; index++)
            {
                _settings.AddVariable(BurstSequenceSettings.ActionKey(index), 0);
                _settings.AddVariable(BurstSequenceSettings.DelayKey(index), 0);
            }

            _settings.Prune();

            InitializePracticeService();
            InitializeEquipment(pluginDir);

            _burstWindow = new BurstSequenceWindowController(
                _settings,
                pluginDir,
                Save,
                StartBurstSequence,
                () =>
                {
                    _autoBurstArmed = false;
                    StopBurstSequence("Burst stopped.");
                },
                SetBurstControlEnabled);

            Chat.RegisterCommand("duelpractice", DuelPracticeCommand);
            Chat.RegisterCommand("duelburst", DuelBurstCommand);
            Duel.Challenged += OnDuelChallenged;
            Duel.StatusChanged += OnDuelStatusChanged;
            Chat.WriteLine("Agent Duel Practice loaded. Use /duelpractice for duel acceptance and /duelburst for the burst editor.");
        }

        protected override bool CanProcessPlayerTarget(SimpleChar fightingTarget)
        {
            return fightingTarget != null && PracticeEnabled && _sessionAccepted
                && _mode != PracticeMode.Punchbag
                && Time.AONormalTime >= _attackReadyAt
                && _opponent != Identity.None
                && fightingTarget.Identity == _opponent;
        }

        protected override void OnUpdate(float deltaTime)
        {
            if (!UpdatePracticeService()) return;
            UpdatePendingDuelAcceptance();
            TrackDuelHealth();
            if (!_sessionAccepted)
            {
                if (IsPracticePreparationActive)
                    base.OnUpdate(deltaTime);

                return;
            }

            if (_mode == PracticeMode.Punchbag) return;
            UpdatePreDuelActions();
            TryStartDuelAttack();
            TryAutoStartBurstSequence();
            PurgeQueuedBurstActions();
            UpdateBurstSequence();
            base.OnUpdate(deltaTime);
        }

        protected override bool ShouldProcessPerkAction(PerkAction perkAction, SimpleChar fightingTarget)
        {
            if (!_sessionAccepted)
                return false;

            if (IsBurstControlledTarget(fightingTarget)
                && BurstActionCatalog.IsManagedPerk((int)perkAction.Hash))
                return false;

            bool allowed = base.ShouldProcessPerkAction(perkAction, fightingTarget);
            if (allowed) _recorder?.Record("actionRequested", new { action = perkAction?.Name, kind = "perk" });
            return allowed;
        }

        protected override bool ShouldProcessItemAction(AOSharp.Core.Inventory.Item item, SimpleChar fightingTarget)
        {
            if (!_sessionAccepted)
                return false;

            if (IsBurstControlledTarget(fightingTarget)
                && BurstActionCatalog.IsManagedItemId(item.Id))
                return false;

            bool allowed = base.ShouldProcessItemAction(item, fightingTarget);
            if (allowed) _recorder?.Record("actionRequested", new { action = item?.Name, kind = "item" });
            return allowed;
        }

        protected override bool ShouldPerformSpecialAttacks(SimpleChar target)
        {
            bool allowed = _mode == PracticeMode.AiOverlord && CanProcessPlayerTarget(target)
                && !IsBurstControlledTarget(target) && base.ShouldPerformSpecialAttacks(target);
            if (allowed) _recorder?.Record("actionRequested", new { action = "special", kind = "weapon_special", target = target.Identity.Instance });
            return allowed;
        }

        private void DuelPracticeCommand(string command, string[] arguments, ChatWindow window)
        {
            var option = arguments.FirstOrDefault()?.ToLowerInvariant();
            if (option == "status") { Chat.WriteLine(PracticeStatus()); return; }
            if (option != null && option != "on" && option != "off")
            {
                Chat.WriteLine("Usage: /duelpractice [on|off|status]. Players: tell help or !help.");
                return;
            }
            var enabled = option == null ? !_settings["DuelPracticeEnabled"].AsBool() : option == "on";
            _settings["DuelPracticeEnabled"] = enabled;
            if (!enabled)
            {
                EndPractice("Operator disabled practice", true);
                ClearPendingDuelAcceptance();
                _opponent = Identity.None;
                ClearAttackSchedule();
                StopBurstSequence("Duel practice disabled.", false);
            }

            Save();
            Chat.WriteLine($"Duel Practice {(enabled ? "enabled" : "disabled")}.");
        }

        private void DuelBurstCommand(string command, string[] arguments, ChatWindow window)
        {
            if (arguments.Length == 0)
            {
                _burstWindow.ToggleWindow();
                return;
            }

            switch (arguments[0].ToLowerInvariant())
            {
                case "run":
                case "start":
                    StartBurstSequence();
                    break;
                case "stop":
                    _autoBurstArmed = false;
                    StopBurstSequence("Burst stopped.");
                    break;
                case "on":
                    SetBurstControlEnabled(true);
                    break;
                case "off":
                    SetBurstControlEnabled(false);
                    break;
                default:
                    Chat.WriteLine("Usage: /duelburst [run|stop|on|off]. Use /duelburst with no argument to open the editor.");
                    break;
            }
        }

        private void OnDuelChallenged(object sender, DuelRequestEventArgs e)
        {
            if (!OwnsPracticeService)
                return;

            // The service issues a challenge only after a tell-selected reservation.
            // Never let an unsolicited /duel bypass recovery or change its opponent.
            e.Decline();
            Reply((uint)e.Challenger.Instance, "Please tell help, then practice 1, 2 or 3. I issue the challenge when ready.");
            return;
        }

        private void OnDuelStatusChanged(object sender, DuelStatusChangedEventArgs e)
        {
            if (!OwnsPracticeService) return;
            _recorder?.Record("duelStatus", new { status = e.Status.ToString(), opponent = e.Opponent.Instance });
            if (e.Status == DuelStatus.Accepted)
            {
                AcceptReservedPractice(e.Opponent);
                return;
            }

            if (e.Opponent != _opponent && e.Opponent != _invitedOpponent) return;

            if (e.Status == DuelStatus.Declined)
            {
                if (_sessionAccepted) return;
                EndPractice("Invitation declined", false);
                ClearPendingDuelAcceptance();
                _opponent = Identity.None;
                ClearAttackSchedule();
                StopBurstSequence("Duel request declined; burst reset.", false);
                return;
            }

            if (e.Status == DuelStatus.Stopped)
            {
                var opponent = e.Opponent != Identity.None ? e.Opponent : _opponent;
                TryRecordDuelOutcome(opponent);
                EndPractice("Duel ended", false);
                ClearPendingDuelAcceptance();
                _opponent = Identity.None;
                ClearAttackSchedule();
                StopBurstSequence("Duel ended; burst reset.", false);
            }
        }

        private void UpdatePendingDuelAcceptance()
        {
            if (_pendingDuelRequest == null)
                return;

            if (_pendingDuelRequest.Responded || !_settings["DuelPracticeEnabled"].AsBool())
            {
                ClearPendingDuelAcceptance();
                return;
            }

            var secondsRemaining = (int)Math.Ceiling(_pendingDuelAcceptAt - Time.AONormalTime);
            if (secondsRemaining <= 0)
            {
                var request = _pendingDuelRequest;
                var challenger = _pendingChallenger;
                var challengerName = _pendingChallengerName;
                ClearPendingDuelAcceptance();

                try
                {
                    ArmDuelAttack(challenger);
                    request.Accept();
                    Chat.WriteLine($"Accepted duel challenge from {challengerName} after the 15-second grace period.");
                }
                catch (Exception exception)
                {
                    _opponent = Identity.None;
                    ClearAttackSchedule();
                    Chat.WriteLine($"Unable to accept the delayed duel request: {exception.Message}");
                }

                return;
            }

            if (secondsRemaining >= _lastAcceptanceSecondsAnnounced)
                return;

            _lastAcceptanceSecondsAnnounced = secondsRemaining;
            if (secondsRemaining == 10)
                SendVicinityMessage("I will accept the duel in 10 seconds.");
            else if (secondsRemaining >= 1 && secondsRemaining <= 5)
                SendVicinityMessage($"{secondsRemaining}...");
        }

        private void ClearPendingDuelAcceptance()
        {
            _pendingDuelRequest = null;
            _pendingChallenger = Identity.None;
            _pendingChallengerName = null;
            _pendingDuelAcceptAt = 0;
            _lastAcceptanceSecondsAnnounced = 0;
        }

        private static void SendVicinityMessage(string message)
        {
            Chat.SendVicinityMessage(message, VicinityMessageType.Vicinity);
        }

        private string ResolvePlayerName(Identity identity)
        {
            return DynelManager.Players.FirstOrDefault(player => player.Identity == identity)?.Name
                ?? $"Opponent {identity.Instance}";
        }

        private DuelRecord GetDuelRecord(Identity opponent, string opponentName = null)
        {
            if (!_duelRecords.TryGetValue(opponent.Instance, out DuelRecord record))
            {
                record = new DuelRecord
                {
                    OpponentId = opponent.Instance,
                    OpponentName = opponentName ?? ResolvePlayerName(opponent)
                };
                _duelRecords[opponent.Instance] = record;
            }
            else if (!string.IsNullOrWhiteSpace(opponentName))
            {
                record.OpponentName = opponentName;
            }

            return record;
        }

        private void LoadDuelRecords()
        {
            try
            {
                if (!File.Exists(_duelRecordPath))
                    return;

                foreach (var line in File.ReadAllLines(_duelRecordPath).Skip(1))
                {
                    var fields = line.Split('\t');
                    if (fields.Length != 4
                        || !int.TryParse(fields[0], out int opponentId)
                        || !int.TryParse(fields[2], out int botWins)
                        || !int.TryParse(fields[3], out int opponentWins))
                        continue;

                    _duelRecords[opponentId] = new DuelRecord
                    {
                        OpponentId = opponentId,
                        OpponentName = fields[1],
                        BotWins = Math.Max(0, botWins),
                        OpponentWins = Math.Max(0, opponentWins)
                    };
                }
            }
            catch (Exception exception)
            {
                Chat.WriteLine($"Unable to load duel records: {exception.Message}");
            }
        }

        private void SaveDuelRecords()
        {
            try
            {
                var lines = new List<string>
                {
                    "OpponentId\tOpponentName\tBotWins\tOpponentWins"
                };

                lines.AddRange(_duelRecords.Values
                    .OrderBy(record => record.OpponentName)
                    .Select(record => string.Join("\t",
                        record.OpponentId,
                        SanitizeRecordName(record.OpponentName),
                        record.BotWins,
                        record.OpponentWins)));

                File.WriteAllLines(_duelRecordPath, lines);
            }
            catch (Exception exception)
            {
                Chat.WriteLine($"Unable to save duel records: {exception.Message}");
            }
        }

        private static string SanitizeRecordName(string name)
        {
            return (name ?? "Unknown")
                .Replace('\t', ' ')
                .Replace('\r', ' ')
                .Replace('\n', ' ');
        }

        private void TrackDuelHealth()
        {
            if (_opponent == Identity.None)
                return;

            var localPlayer = DynelManager.LocalPlayer;
            _minimumLocalDuelHealth = Math.Min(_minimumLocalDuelHealth, localPlayer.Health);

            var opponent = DynelManager.Players.FirstOrDefault(player => player.Identity == _opponent);
            if (opponent != null)
                _minimumOpponentDuelHealth = Math.Min(_minimumOpponentDuelHealth, opponent.Health);
        }

        private void TryRecordDuelOutcome(Identity opponentIdentity)
        {
            if (_duelOutcomeRecorded || opponentIdentity == Identity.None)
                return;

            TrackDuelHealth();
            _duelOutcomeRecorded = true;

            var localHealth = Math.Min(_minimumLocalDuelHealth, DynelManager.LocalPlayer.Health);
            var opponent = DynelManager.Players.FirstOrDefault(player => player.Identity == opponentIdentity);
            var opponentHealth = opponent == null
                ? _minimumOpponentDuelHealth
                : Math.Min(_minimumOpponentDuelHealth, opponent.Health);

            var botLost = localHealth <= 1;
            var opponentLost = opponentHealth <= 1;

            if (botLost == opponentLost)
            {
                Chat.WriteLine("Duel ended without a decisive duel-floor health result; record unchanged.");
                return;
            }

            var opponentName = opponent?.Name ?? _activeOpponentName ?? ResolvePlayerName(opponentIdentity);
            var record = GetDuelRecord(opponentIdentity, opponentName);
            if (opponentLost)
                record.BotWins++;
            else
                record.OpponentWins++;

            SaveDuelRecords();
            Chat.WriteLine($"Duel record versus {record.OpponentName}: {record.BotWins}-{record.OpponentWins}.");
        }

        private void SetBurstControlEnabled(bool enabled)
        {
            _settings["DuelBurstControlEnabled"] = enabled;

            if (!enabled)
            {
                _autoBurstArmed = false;
                StopBurstSequence("Burst control disabled.", false);
            }
            else if (_opponent != Identity.None && _attackReadyAt > 0)
            {
                _autoBurstArmed = true;
            }

            Save();
            _burstWindow.RefreshControlState();
            Chat.WriteLine(enabled
                ? "Burst control enabled. The saved sequence is armed to run automatically when normal attack starts against the accepted duel opponent."
                : "Burst control disabled. Normal automatic offensive processing restored.");
        }

        private bool IsBurstControlledTarget(SimpleChar target)
        {
            return target != null
                && BurstEnabled
                && (_burstRunning || _autoBurstArmed)
                && CanProcessPlayerTarget(target);
        }

        private void PurgeQueuedBurstActions()
        {
            if (!IsBurstControlledTarget(DynelManager.LocalPlayer.FightingTarget))
                return;

            _actionQueue = new Queue<CombatActionQueueItem>(_actionQueue.Where(queueItem =>
                !(queueItem.CombatAction is PerkAction perkAction
                    && BurstActionCatalog.IsManagedPerk((int)perkAction.Hash))
                && !(queueItem.CombatAction is AOSharp.Core.Inventory.Item item
                    && BurstActionCatalog.IsManagedItemId(item.Id))));
        }

        private void StartBurstSequence()
        {
            if (!BurstEnabled || !CanProcessPlayerTarget(ResolveOpponent()))
            {
                ReportBurstStatus("Enable Burst Control before running the sequence.");
                return;
            }

            var opponent = ResolveOpponent();
            var localPlayer = DynelManager.LocalPlayer;

            if (opponent == null
                || !localPlayer.IsAttacking
                || localPlayer.FightingTarget?.Identity != _opponent)
            {
                ReportBurstStatus("Run Burst requires an active normal attack against the accepted duel opponent.");
                return;
            }

            _activeBurstSteps = LoadBurstSteps();
            if (_activeBurstSteps.Count == 0)
            {
                ReportBurstStatus("No offensive actions are configured in the burst sequence.");
                return;
            }

            _burstRunning = true;
            _autoBurstArmed = false;
            _activeBurstStepIndex = 0;
            ArmCurrentBurstStep();
            ReportBurstStatus($"Burst started with {_activeBurstSteps.Count} configured actions.");
        }

        private void TryAutoStartBurstSequence()
        {
            if (!_autoBurstArmed || !BurstEnabled)
                return;

            var localPlayer = DynelManager.LocalPlayer;
            if (_opponent == Identity.None
                || !localPlayer.IsAttacking
                || localPlayer.FightingTarget?.Identity != _opponent)
                return;

            // Consume the one-shot arm before starting so an empty or temporarily
            // invalid sequence cannot generate a failure message every update.
            _autoBurstArmed = false;
            StartBurstSequence();
        }

        private void UpdateBurstSequence()
        {
            if (!_burstRunning)
                return;

            if (!BurstEnabled)
            {
                StopBurstSequence("Burst control was disabled.", false);
                return;
            }

            var opponent = ResolveOpponent();
            var localPlayer = DynelManager.LocalPlayer;

            if (opponent == null
                || !localPlayer.IsAttacking
                || localPlayer.FightingTarget?.Identity != _opponent)
            {
                StopBurstSequence("Burst cancelled because the accepted duel target is no longer being attacked.");
                return;
            }

            if (Time.AONormalTime < _nextBurstStepAt)
                return;

            var step = _activeBurstSteps[_activeBurstStepIndex];
            if (step.ActionCode > 0 && HasObservedEvasionDefense(opponent))
            {
                // A tactical hold must not consume the step's execution timeout.
                _burstStepDeadline = Time.AONormalTime + _settings["DuelBurstStepTimeoutMs"].AsInt32() / 1000d;
                RecordDecision("Holding burst perk while Dance of Fools is observed");
                return;
            }
            if (TryExecuteBurstStep(step, opponent))
            {
                _recorder?.Record("actionRequested", new { action = GetActionLabel(step.ActionCode), lane = "burst" });
                Chat.WriteLine($"Burst {_activeBurstStepIndex + 1}/{_activeBurstSteps.Count}: {GetActionLabel(step.ActionCode)}");
                AdvanceBurstStep();
                return;
            }

            if (Time.AONormalTime >= _burstStepDeadline)
            {
                Chat.WriteLine($"Burst skipped after timeout: {GetActionLabel(step.ActionCode)}");
                AdvanceBurstStep();
            }
        }

        private bool TryExecuteBurstStep(BurstSequenceStep step, SimpleChar opponent)
        {
            if (!CanProcessPlayerTarget(opponent)) return false;
            if (step.ActionCode > 0)
                return TryUseBurstPerk((PerkHash)step.ActionCode, opponent);

            if (BurstActionCatalog.TryGetSpecial(step.ActionCode, out SpecialAttack specialAttack))
                return TryUseSpecialAttack(opponent, specialAttack);

            if (BurstActionCatalog.TryGetItemId(step.ActionCode, out int itemId))
                return TryUseTargetedItem(itemId, opponent);

            return false;
        }

        private bool TryUseBurstPerk(PerkHash perkHash, SimpleChar opponent)
        {
            if (perkHash != PerkHash.ConcussiveShot)
                return TryUseRegisteredPerk(perkHash, opponent);

            var rootsEnabled = _settings["Roots/Snares/Stuns"].AsBool();
            var concussiveEnabled = _settings["PerkConcussiveShot"].AsBool();

            try
            {
                _settings["Roots/Snares/Stuns"] = true;
                _settings["PerkConcussiveShot"] = true;
                return TryUseRegisteredPerk(perkHash, opponent);
            }
            finally
            {
                _settings["Roots/Snares/Stuns"] = rootsEnabled;
                _settings["PerkConcussiveShot"] = concussiveEnabled;
            }
        }

        private void ArmPreDuelActions()
        {
            _preDuelActions.Clear();
            AddPreDuelAction(
                BurstSequenceSettings.PreMyOwnFortressEnabled,
                BurstSequenceSettings.PreMyOwnFortressAtMs,
                PreDuelActionKind.MyOwnFortress,
                "My Own Fortress");
            AddPreDuelAction(
                BurstSequenceSettings.PreBreakOutEnabled,
                BurstSequenceSettings.PreBreakOutAtMs,
                PreDuelActionKind.BreakOut,
                "Break Out");
            AddPreDuelAction(
                BurstSequenceSettings.PreRecalibrateEnabled,
                BurstSequenceSettings.PreRecalibrateAtMs,
                PreDuelActionKind.Recalibrate,
                "Recalibrate");
            AddPreDuelAction(
                BurstSequenceSettings.PreAbsoluteConcentrationEnabled,
                BurstSequenceSettings.PreAbsoluteConcentrationAtMs,
                PreDuelActionKind.AbsoluteConcentration,
                "Absolute Concentration");

            _preDuelStartedAt = Time.AONormalTime;
            _preDuelActive = _preDuelActions.Count > 0;
        }

        private void AddPreDuelAction(string enabledKey, string triggerKey, PreDuelActionKind kind, string name)
        {
            if (!_settings[enabledKey].AsBool())
                return;

            var triggerAtMs = Math.Max(0, Math.Min(
                BurstSequenceSettings.MaxPreDuelTriggerMs,
                _settings[triggerKey].AsInt32()));
            _preDuelActions.Add(new PreDuelActionState(kind, name, triggerAtMs));
        }

        private void UpdatePreDuelActions()
        {
            if (!_preDuelActive)
                return;

            if (!_settings["DuelPracticeEnabled"].AsBool()
                || _opponent == Identity.None
                || _attackReadyAt <= 0)
            {
                ClearPreDuelActions();
                return;
            }

            var elapsedMs = (int)Math.Max(0, (Time.AONormalTime - _preDuelStartedAt) * 1000d);

            foreach (var action in _preDuelActions.Where(action =>
                !action.Complete && elapsedMs >= action.TriggerAtMs))
            {
                if (!TryUsePreDuelAction(action.Kind))
                    continue;

                action.Complete = true;
                Chat.WriteLine($"Pre-duel prepared: {action.Name} at {elapsedMs} ms.");
            }

            if (_preDuelActions.All(action => action.Complete))
            {
                _preDuelActive = false;
                return;
            }

            if (Time.AONormalTime < _attackReadyAt)
                return;

            foreach (var action in _preDuelActions.Where(action => !action.Complete))
                Chat.WriteLine($"Pre-duel skipped: {action.Name} was unavailable before combat started.");

            _preDuelActive = false;
        }

        private bool TryUsePreDuelAction(PreDuelActionKind kind)
        {
            switch (kind)
            {
                case PreDuelActionKind.MyOwnFortress:
                    return TryUsePreDuelSelfPerk(PerkHash.MyOwnFortress);
                case PreDuelActionKind.BreakOut:
                    return TryCastPreDuelSelfNano(BreakOutSpellId);
                case PreDuelActionKind.Recalibrate:
                    return TryUsePreDuelSelfPerk(PerkHash.Recalibrate);
                case PreDuelActionKind.AbsoluteConcentration:
                    return TryCastPreDuelSelfNano(AbsoluteConcentrationSpellId);
                default:
                    return false;
            }
        }

        private bool TryUsePreDuelSelfPerk(PerkHash perkHash)
        {
            if (!PerkAction.Find(perkHash, out PerkAction perkAction))
                return false;

            var localPlayer = DynelManager.LocalPlayer;
            if (localPlayer.Buffs.Any(buff => buff.Name == perkAction.Name))
                return true;

            if (PerkAction.List.Count(perk => perk.IsExecuting || perk.IsPending)
                >= _settings["MAX_CONCURRENT_PERKS"].AsInt32())
                return false;

            return CanUsePerk(perkAction) && perkAction.Use(localPlayer, true);
        }

        private bool TryCastPreDuelSelfNano(int spellId)
        {
            if (!Spell.Find(spellId, out Spell spell))
                return false;

            var localPlayer = DynelManager.LocalPlayer;
            if (localPlayer.Buffs.Any(buff =>
                buff.Nanoline == spell.Nanoline && buff.StackingOrder >= spell.StackingOrder))
                return true;

            if (!localPlayer.MovementStatePermitsCasting || !CanCast(spell))
                return false;

            spell.Cast(localPlayer, true);
            return true;
        }

        private void ClearPreDuelActions()
        {
            _preDuelActive = false;
            _preDuelStartedAt = 0;
            _preDuelActions.Clear();
        }

        private void AdvanceBurstStep()
        {
            _activeBurstStepIndex++;
            if (_activeBurstStepIndex >= _activeBurstSteps.Count)
            {
                _burstRunning = false;
                _activeBurstSteps.Clear();
                ReportBurstStatus("Burst sequence complete.");
                return;
            }

            ArmCurrentBurstStep();
        }

        private void ArmCurrentBurstStep()
        {
            var step = _activeBurstSteps[_activeBurstStepIndex];
            _nextBurstStepAt = Time.AONormalTime + (step.DelayMs / 1000d);
            _burstStepDeadline = _nextBurstStepAt
                + (_settings["DuelBurstStepTimeoutMs"].AsInt32() / 1000d);
        }

        private List<BurstSequenceStep> LoadBurstSteps()
        {
            var steps = new List<BurstSequenceStep>();

            for (var index = 0; index < BurstSequenceSettings.SlotCount; index++)
            {
                var actionCode = _settings[BurstSequenceSettings.ActionKey(index)].AsInt32();
                if (actionCode == 0)
                    continue;

                var delayMs = Math.Max(0, Math.Min(60000,
                    _settings[BurstSequenceSettings.DelayKey(index)].AsInt32()));
                steps.Add(new BurstSequenceStep(actionCode, delayMs));
            }

            return steps;
        }

        private string GetActionLabel(int actionCode)
        {
            if (actionCode > 0
                && PerkAction.Find((PerkHash)actionCode, out PerkAction perkAction))
                return perkAction.Name;

            return BurstActionCatalog.GetActionLabel(actionCode);
        }

        private SimpleChar ResolveOpponent()
        {
            if (_opponent == Identity.None)
                return null;

            return DynelManager.Players.FirstOrDefault(player =>
                player.Identity == _opponent && player.IsAlive);
        }

        private void StopBurstSequence(string status, bool writeToChat = true)
        {
            _burstRunning = false;
            _activeBurstSteps.Clear();
            _activeBurstStepIndex = 0;
            _nextBurstStepAt = 0;
            _burstStepDeadline = 0;

            _burstWindow?.SetStatus(status);
            if (writeToChat)
                Chat.WriteLine(status);
        }

        private void ReportBurstStatus(string status)
        {
            _burstWindow?.SetStatus(status);
            Chat.WriteLine(status);
        }

        private void TryStartDuelAttack()
        {
            if (_mode == PracticeMode.Punchbag || !_sessionAccepted) return;
            if (_attackReadyAt <= 0)
                return;

            if (!_settings["DuelPracticeEnabled"].AsBool() || _opponent == Identity.None)
            {
                ClearAttackSchedule();
                return;
            }

            var opponent = ResolveOpponent();

            if (opponent == null)
                return;

            var localPlayer = DynelManager.LocalPlayer;

            if (localPlayer.IsAttacking && localPlayer.FightingTarget?.Identity == _opponent)
                return;

            // An opponent attacking us is definitive proof that the countdown has
            // ended, even if the local timing or duel notification arrived late.
            if (opponent.IsAttacking && opponent.FightingTarget?.Identity == localPlayer.Identity
                && Time.AONormalTime < _attackReadyAt)
            {
                _attackReadyAt = Time.AONormalTime;
                _nextAttackAttempt = Time.AONormalTime;
            }

            if (Time.AONormalTime < _attackReadyAt || Time.AONormalTime < _nextAttackAttempt)
                return;

            // Mirror Manager.Attack's proven target-then-attack sequence. If we
            // somehow entered the duel while attacking something else, stop first
            // and switch cleanly on the following attempt.
            if (localPlayer.IsAttacking && localPlayer.FightingTarget?.Identity != _opponent)
            {
                localPlayer.StopAttack(false);
                _nextAttackAttempt = Time.AONormalTime + AttackRetryIntervalSeconds;
                return;
            }

            if (localPlayer.IsAttackPending)
                return;

            if (Targeting.Target?.Identity != _opponent)
                Targeting.SetTarget(opponent, false);

            localPlayer.Attack(_opponent);

            _nextAttackAttempt = Time.AONormalTime + AttackRetryIntervalSeconds;
        }

        private void ArmDuelAttack(Identity opponent)
        {
            _opponent = opponent;
            _activeOpponentName = ResolvePlayerName(opponent);
            _minimumLocalDuelHealth = DynelManager.LocalPlayer.Health;
            var opponentCharacter = DynelManager.Players.FirstOrDefault(player => player.Identity == opponent);
            _minimumOpponentDuelHealth = opponentCharacter?.Health ?? int.MaxValue;
            _duelOutcomeRecorded = false;
            _attackReadyAt = Time.AONormalTime + DuelCountdownSeconds;
            _nextAttackAttempt = _attackReadyAt;
            _autoBurstArmed = _mode == PracticeMode.AiOverlord;
            if (_mode == PracticeMode.AiOverlord) ArmPreDuelActions();
        }

        private void ClearAttackSchedule()
        {
            _attackReadyAt = 0;
            _nextAttackAttempt = 0;
            _autoBurstArmed = false;
            ClearPreDuelActions();
            _activeOpponentName = null;
            _minimumLocalDuelHealth = int.MaxValue;
            _minimumOpponentDuelHealth = int.MaxValue;
        }
    }
}
