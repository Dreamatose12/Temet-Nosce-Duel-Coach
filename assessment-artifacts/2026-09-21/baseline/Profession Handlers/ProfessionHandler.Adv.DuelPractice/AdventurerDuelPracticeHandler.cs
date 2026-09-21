using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AOSharp.Common.GameData;
using AOSharp.Common.GameData.UI;
using AOSharp.Core;
using AOSharp.Core.Movement;
using AOSharp.Core.UI;
using ProfessionHandler.Generic;

namespace ProfessionHandler.Adventurer.DuelPractice
{
    public class AdventurerDuelPracticeHandler : GenericProfessionHandler
    {
        private const double DuelCountdownSeconds = 4.0;
        private const double DuelAcceptanceDelaySeconds = 15.0;
        private const double AttackRetryIntervalSeconds = 0.01;

        private sealed class DuelRecord
        {
            public int OpponentId { get; set; }
            public string OpponentName { get; set; }
            public int BotWins { get; set; }
            public int OpponentWins { get; set; }
        }

        private Identity _opponent = Identity.None;
        private bool _duelAccepted;

        private DuelRequestEventArgs _pendingDuelRequest;
        private Identity _pendingChallenger = Identity.None;
        private string _pendingChallengerName;
        private double _pendingDuelAcceptAt;
        private int _lastAcceptanceSecondsAnnounced;

        private double _attackReadyAt;
        private double _nextAttackAttempt;

        private readonly Dictionary<int, DuelRecord> _duelRecords = new Dictionary<int, DuelRecord>();
        private readonly string _duelRecordPath;
        private string _activeOpponentName;
        private int _minimumLocalDuelHealth = int.MaxValue;
        private int _minimumOpponentDuelHealth = int.MaxValue;
        private bool _duelOutcomeRecorded;
        private readonly string _pluginDirectory;

        public AdventurerDuelPracticeHandler(string pluginDir) : base(pluginDir)
        {
            _pluginDirectory = pluginDir;
            _duelRecordPath = System.IO.Path.Combine(pluginDir, "AdvDuelPracticeRecords.tsv");
            LoadDuelRecords();

            _settings.AddVariable("DuelPracticeEnabled", false);
            _settings.Prune();

            Chat.RegisterCommand("handler", HandlerCommand);
            Chat.RegisterCommand("duelpractice", DuelPracticeCommand);
            Duel.Challenged += OnDuelChallenged;
            Duel.StatusChanged += OnDuelStatusChanged;

            Chat.WriteLine("220 Adventurer Duel Practice loaded. Use /duelpractice on to begin accepting challenges.");
        }

        protected override bool CanProcessPlayerTarget(SimpleChar fightingTarget)
        {
            return fightingTarget != null
                && IsDuelPracticeActive
                && _duelAccepted
                && _opponent != Identity.None
                && fightingTarget.Identity == _opponent;
        }

        protected override bool ShouldRunWhenHandlerDisabled()
        {
            // Keep the fail-closed lifecycle running so /HandlerEnable cannot
            // strand an automated attack after it disables generic processing.
            return true;
        }

        protected override bool ShouldProcessPerkAction(PerkAction perkAction, SimpleChar fightingTarget)
        {
            if (!IsDuelPracticeActive)
                return false;

            if (perkAction.Hash == PerkHash.Grasp
                || perkAction.Hash == PerkHash.Bearhug
                || perkAction.Hash == PerkHash.GripOfColossus
                || perkAction.Hash == PerkHash.Stoneworks
                || perkAction.Hash == PerkHash.BioRejuvenation
                || perkAction.Hash == PerkHash.Awakening
                || perkAction.Hash == PerkHash.Sphere
                || perkAction.Hash == PerkHash.Beckoning)
            {
                return false;
            }

            return base.ShouldProcessPerkAction(perkAction, fightingTarget);
        }

        protected override bool ShouldProcessItemAction(AOSharp.Core.Inventory.Item item, SimpleChar fightingTarget)
        {
            if (!IsDuelPracticeActive)
                return false;

            // Keep the POC to self-preservation items. Damage shoulders (including
            // Might of the Revenant), grenades, sharp objects, and area items wait
            // for an explicitly agreed Adventurer opening policy.
            return RelevantGenericItems.HealthAndNanoStims.Contains(item.Id)
                || RelevantGenericItems.FreeStims.Contains(item.Id);
        }

        protected override bool ShouldProcessSpellAction(Spell spell, SimpleChar fightingTarget)
        {
            if (!IsDuelPracticeActive
                || SpellID.AdventurerTeamHealing.Contains(spell.Id)
                || SpellID.TeamFortifyAdventurer.Contains(spell.Id)
                || spell.Nanoline == NanoLine.TeamRunSpeedBuffs)
                return false;

            return base.ShouldProcessSpellAction(spell, fightingTarget);
        }

        protected override bool ShouldProcessActionTarget(SimpleChar fightingTarget, SimpleChar actionTarget)
        {
            if (!IsDuelPracticeActive)
                return false;

            if (actionTarget == null)
                return true;

            return actionTarget.Identity == DynelManager.LocalPlayer.Identity
                || (_duelAccepted && actionTarget.Identity == _opponent);
        }

        protected override bool ShouldProcessItemActionTarget(
            AOSharp.Core.Inventory.Item item,
            SimpleChar fightingTarget,
            SimpleChar actionTarget)
        {
            if (!IsDuelPracticeActive)
                return false;

            return actionTarget == null
                || actionTarget.Identity == DynelManager.LocalPlayer.Identity;
        }

        protected override bool ShouldProcessSpellActionTarget(
            Spell spell,
            SimpleChar fightingTarget,
            SimpleChar actionTarget)
        {
            if (!IsDuelPracticeActive)
                return false;

            // Keep the first POC from casting target buffs on the opponent.
            // Opponent-targeted nanos can be added later as an explicit hostile
            // allowlist once the level-220 rotation is agreed.
            return actionTarget == null
                || actionTarget.Identity == DynelManager.LocalPlayer.Identity;
        }

        protected override bool ShouldProcessExplicitPerkAction(PerkAction perkAction, SimpleChar fightingTarget)
        {
            return false;
        }

        protected override bool ShouldProcessExplicitItemAction(
            AOSharp.Core.Inventory.Item item,
            SimpleChar fightingTarget)
        {
            return false;
        }

        protected override bool ShouldRunGenericHousekeeping()
        {
            return false;
        }

        protected override void OnUpdate(float deltaTime)
        {
            var runGenericProcessing = false;

            try
            {
                if (!IsDuelPracticeActive)
                {
                    ClearPendingDuelAcceptance();

                    if (_duelAccepted || _opponent != Identity.None)
                        ClearDuelState(true);
                    else
                        _actionQueue.Clear();

                    return;
                }

                if (!Game.IsZoning)
                {
                    EnforceStationaryDuelSettings();
                    UpdatePendingDuelAcceptance();
                    TrackDuelHealth();
                    TryStartDuelAttack();

                    if (IsDuelPracticeActive)
                    {
                        var fightingTarget = DynelManager.LocalPlayer.FightingTarget;
                        if (fightingTarget != null && !CanProcessPlayerTarget(fightingTarget))
                        {
                            if (DynelManager.LocalPlayer.IsAttacking)
                                DynelManager.LocalPlayer.StopAttack(false);

                            _actionQueue.Clear();
                            runGenericProcessing = false;
                        }
                        else
                        {
                            runGenericProcessing = true;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                ErrorCatch(exception);
                return;
            }

            if (runGenericProcessing)
                base.OnUpdate(deltaTime);
        }

        private void HandlerCommand(string command, string[] arguments, ChatWindow window)
        {
            try
            {
                if (_mainWindow?.IsValid == true)
                {
                    Window_Closed_helper();
                    _mainWindow.Close();
                    _mainWindow = null;
                    return;
                }

                _mainWindow = Window.CreateFromXml(
                    "Adv Duel Practice",
                    _pluginDirectory + "\\UI\\AdvDuelPracticeSettingsView.xml",
                    windowStyle: WindowStyle.Default,
                    windowFlags: WindowFlags.AutoScale | WindowFlags.NoFade);
                _mainWindow.MoveTo(_settings["MainWindowTopLeftX"].AsFloat(), _settings["MainWindowTopLeftY"].AsFloat());

                if (_mainWindow.FindView("DuelPracticeToggle", out Button duelPracticeButton))
                {
                    RefreshDuelPracticeButton(duelPracticeButton);
                    duelPracticeButton.Clicked = (sender, button) =>
                    {
                        SetDuelPracticeEnabled(!_settings["DuelPracticeEnabled"].AsBool());
                        RefreshDuelPracticeButton(duelPracticeButton);
                    };
                }

                if (_mainWindow.FindView("SpecialsToggle", out Button specialsButton))
                {
                    RefreshSpecialsButton(specialsButton);
                    specialsButton.Clicked = (sender, button) =>
                    {
                        _settings["Specials"] = !_settings["Specials"].AsBool();
                        Save();
                        RefreshSpecialsButton(specialsButton);
                    };
                }

                if (_mainWindow.FindView("ReserveNCU", out TextInputView reserveNcu))
                    reserveNcu.Text = _settings["Reserve NCU"].AsInt32().ToString();

                if (_mainWindow.FindView("SaveNCUs", out Button saveNcus))
                    saveNcus.Clicked = Save_Ncus_Button_Clicked;

                if (_mainWindow.FindView("MAX_CONCURRENT_PERKS", out TextInputView maxPerks))
                    maxPerks.Text = _settings["MAX_CONCURRENT_PERKS"].AsInt32().ToString();

                if (_mainWindow.FindView("SavePerks", out Button savePerks))
                    savePerks.Clicked = Save_Perks_Button_Clicked;

                if (_mainWindow.FindView("DynamicButtons", out View dynamicButtons))
                    BuildDynamicButtons(dynamicButtons);

                if (_mainWindow.FindView("Errors", out View errorView))
                    PopulateErrorView(errorView);

                if (_mainWindow.FindView("VersionNumber", out TextView version))
                    version.Text = $"Generic engine {_settings["Version_Number"].AsFloat()} / Adventurer duel POC";

                _mainWindow.Show(true);
            }
            catch (Exception exception)
            {
                ErrorCatch(exception);
            }
        }

        private static void RefreshDuelPracticeButton(Button button)
        {
            button.SetLabel(_settings["DuelPracticeEnabled"].AsBool()
                ? "Duel Practice: ON"
                : "Duel Practice: OFF");
        }

        private static void RefreshSpecialsButton(Button button)
        {
            button.SetLabel(_settings["Specials"].AsBool()
                ? "Weapon Specials: ON"
                : "Weapon Specials: OFF");
        }

        private void DuelPracticeCommand(string command, string[] arguments, ChatWindow window)
        {
            var current = _settings["DuelPracticeEnabled"].AsBool();
            bool enabled;

            if (arguments.Length == 0)
            {
                enabled = !current;
            }
            else
            {
                switch (arguments[0].ToLowerInvariant())
                {
                    case "on":
                    case "start":
                    case "enable":
                        enabled = true;
                        break;
                    case "off":
                    case "stop":
                    case "disable":
                        enabled = false;
                        break;
                    case "status":
                        WriteStatus();
                        return;
                    default:
                        Chat.WriteLine("Usage: /duelpractice [on|off|status]");
                        return;
                }
            }

            SetDuelPracticeEnabled(enabled);
        }

        private void SetDuelPracticeEnabled(bool enabled)
        {
            _settings["DuelPracticeEnabled"] = enabled;

            if (enabled)
            {
                _actionQueue.Clear();
                EnforceStationaryDuelSettings();
            }
            else
            {
                ClearPendingDuelAcceptance();
                ClearDuelState(true);
            }

            Save();
            Chat.WriteLine($"Adventurer Duel Practice {(enabled ? "enabled" : "disabled")}.");
        }

        private void WriteStatus()
        {
            var enabled = _settings["DuelPracticeEnabled"].AsBool();
            var opponent = _opponent == Identity.None ? "none" : ResolvePlayerName(_opponent);
            var pending = _pendingChallenger == Identity.None ? "none" : _pendingChallengerName;

            Chat.WriteLine(
                $"Adventurer Duel Practice: {(enabled ? "enabled" : "disabled")}; "
                + $"accepted opponent: {opponent}; pending challenger: {pending}.");
        }

        private void EnforceStationaryDuelSettings()
        {
            if (!IsDuelPracticeActive)
                return;

            HaltMovement();

            // Backstab/get-behind logic must never reposition the practice bot.
            if (_settings["ShouldMoveBehindTarget"].AsInt32() != 0)
                _settings["ShouldMoveBehindTarget"] = 0;

            // Keep area actions disabled around bystanders. Generic PVPDistance
            // checks remain in force as a second safety layer.
            if (_settings["AOE"].AsBool())
                _settings["AOE"] = false;

            // Dedicated duel practice never heals teammates. A selected
            // Adventurer heal remains opt-in, but its target mode is self-only.
            if (_settings["AdventureSingleTargetHealingOption"].AsInt32() != 1)
                _settings["AdventureSingleTargetHealingOption"] = 1;
            if (_settings["AdventurerCompleteHealingOption"].AsInt32() != 1)
                _settings["AdventurerCompleteHealingOption"] = 1;
            if (_settings["MorphHealOption"].AsInt32() != 1)
                _settings["MorphHealOption"] = 1;
            if (_settings["AdventurerTeamHealing"].AsInt32() != 0)
                _settings["AdventurerTeamHealing"] = 0;
            if (_settings["AdventurerTeamHealingOption"].AsInt32() != 0)
                _settings["AdventurerTeamHealingOption"] = 0;
        }

        private void OnDuelChallenged(object sender, DuelRequestEventArgs request)
        {
            if (!IsDuelPracticeActive)
                return;

            if (_duelAccepted || _opponent != Identity.None)
            {
                Chat.WriteLine("A duel is already active; the additional request was left unanswered.");
                return;
            }

            if (_pendingDuelRequest != null && !_pendingDuelRequest.Responded)
            {
                Chat.WriteLine("A duel acceptance countdown is already active; the additional request was left unanswered.");
                return;
            }

            _pendingDuelRequest = request;
            _pendingChallenger = request.Challenger;
            _pendingChallengerName = ResolvePlayerName(request.Challenger);
            _pendingDuelAcceptAt = Time.AONormalTime + DuelAcceptanceDelaySeconds;
            _lastAcceptanceSecondsAnnounced = (int)DuelAcceptanceDelaySeconds;

            var record = GetDuelRecord(request.Challenger, _pendingChallengerName);
            SendVicinityMessage(
                $"Good luck my brother, so far we are {record.BotWins}-{record.OpponentWins}. I will accept in 15 seconds.");
            Chat.WriteLine($"Duel request from {_pendingChallengerName} queued for acceptance in 15 seconds.");
        }

        private void OnDuelStatusChanged(object sender, DuelStatusChangedEventArgs status)
        {
            if (!IsDuelPracticeActive)
            {
                ClearPendingDuelAcceptance();

                if (_duelAccepted || _opponent != Identity.None)
                    ClearDuelState(true);

                return;
            }

            if (status.Status == DuelStatus.Accepted)
            {
                if (_duelAccepted && _opponent != Identity.None)
                {
                    if (status.Opponent != Identity.None && status.Opponent != _opponent)
                        return;

                    Chat.WriteLine("Duel accepted. Attack attempts begin at 4 seconds and repeat every 10 ms until successful.");
                    return;
                }

                var expectedOpponent = _pendingChallenger;
                if (expectedOpponent == Identity.None)
                    return;

                if (status.Opponent != Identity.None
                    && status.Opponent != expectedOpponent)
                {
                    return;
                }

                var opponent = status.Opponent != Identity.None
                    ? status.Opponent
                    : expectedOpponent;

                if (opponent == Identity.None)
                    return;

                ClearPendingDuelAcceptance();
                ArmDuelAttack(opponent);

                Chat.WriteLine("Duel accepted. Attack attempts begin at 4 seconds and repeat every 10 ms until successful.");
                return;
            }

            if (status.Status == DuelStatus.Declined)
            {
                if (_duelAccepted || _pendingChallenger == Identity.None)
                    return;

                if (status.Opponent != Identity.None && status.Opponent != _pendingChallenger)
                    return;

                ClearPendingDuelAcceptance();
                ClearDuelState(true);
                return;
            }

            if (status.Status == DuelStatus.Stopped)
            {
                if (!_duelAccepted || _opponent == Identity.None)
                    return;

                if (status.Opponent != Identity.None && status.Opponent != _opponent)
                    return;

                TryRecordDuelOutcome(_opponent);
                ClearPendingDuelAcceptance();
                ClearDuelState();
            }
        }

        private void UpdatePendingDuelAcceptance()
        {
            if (_pendingDuelRequest == null)
                return;

            if (_pendingDuelRequest.Responded || !IsDuelPracticeActive)
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

                try
                {
                    request.Accept();
                    if (!_duelAccepted || _opponent != challenger)
                        ArmDuelAttack(challenger);
                    ClearPendingDuelAcceptance();
                    Chat.WriteLine($"Accepted duel challenge from {challengerName} after the 15-second grace period.");
                }
                catch (Exception exception)
                {
                    ClearPendingDuelAcceptance();
                    ClearDuelState(true);
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

        private void TryStartDuelAttack()
        {
            if (_attackReadyAt <= 0 || !_duelAccepted)
                return;

            if (!IsDuelPracticeActive || _opponent == Identity.None)
            {
                ClearDuelState(true);
                return;
            }

            if (Time.AONormalTime < _attackReadyAt || Time.AONormalTime < _nextAttackAttempt)
                return;

            var opponent = ResolveOpponent();
            if (opponent == null || !opponent.IsAlive)
            {
                _nextAttackAttempt = Time.AONormalTime + AttackRetryIntervalSeconds;
                return;
            }

            var localPlayer = DynelManager.LocalPlayer;
            if (localPlayer.IsAttacking && localPlayer.FightingTarget?.Identity == _opponent)
                return;

            if (localPlayer.IsAttacking && localPlayer.FightingTarget?.Identity != _opponent)
            {
                localPlayer.StopAttack(false);
                _nextAttackAttempt = Time.AONormalTime + AttackRetryIntervalSeconds;
                return;
            }

            if (localPlayer.IsAttackPending)
                return;

            Targeting.SetTarget(opponent);
            localPlayer.Attack(opponent, false);
            _nextAttackAttempt = Time.AONormalTime + AttackRetryIntervalSeconds;
        }

        private void ArmDuelAttack(Identity opponent)
        {
            if (opponent == Identity.None)
                return;

            _opponent = opponent;
            _duelAccepted = true;
            _activeOpponentName = ResolvePlayerName(opponent);
            _minimumLocalDuelHealth = DynelManager.LocalPlayer.Health;

            var opponentCharacter = ResolveOpponent();
            _minimumOpponentDuelHealth = opponentCharacter?.Health ?? int.MaxValue;
            _duelOutcomeRecorded = false;

            _attackReadyAt = Time.AONormalTime + DuelCountdownSeconds;
            _nextAttackAttempt = _attackReadyAt;
        }

        private SimpleChar ResolveOpponent()
        {
            if (_opponent == Identity.None)
                return null;

            return DynelManager.GetDynel(_opponent) as SimpleChar
                ?? DynelManager.Players.FirstOrDefault(player => player.Identity == _opponent);
        }

        private void ClearDuelState(bool suppressPendingOutcome = false)
        {
            if (suppressPendingOutcome)
                _duelOutcomeRecorded = true;

            HaltMovement();
            StopTrackedDuelAttack();
            _actionQueue.Clear();
            _opponent = Identity.None;
            _duelAccepted = false;
            _attackReadyAt = 0;
            _nextAttackAttempt = 0;
            _activeOpponentName = null;
            _minimumLocalDuelHealth = int.MaxValue;
            _minimumOpponentDuelHealth = int.MaxValue;
        }

        private void StopTrackedDuelAttack()
        {
            if (_opponent == Identity.None)
                return;

            var localPlayer = DynelManager.LocalPlayer;
            var fightingTarget = localPlayer.FightingTarget;

            if ((localPlayer.IsAttacking || localPlayer.IsAttackPending)
                && (fightingTarget == null || fightingTarget.Identity == _opponent))
            {
                localPlayer.StopAttack(false);
            }
        }

        private void HaltMovement()
        {
            try
            {
                MovementController.Instance.Halt();
            }
            catch (Exception exception)
            {
                ErrorCatch(exception);
            }
        }

        private bool IsDuelPracticeActive =>
            _settings["DuelPracticeEnabled"].AsBool()
            && _settings["Enable"].AsBool();

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
            return (DynelManager.GetDynel(identity) as SimpleChar)?.Name
                ?? DynelManager.Players.FirstOrDefault(player => player.Identity == identity)?.Name
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
                    {
                        continue;
                    }

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
                Chat.WriteLine($"Unable to load Adventurer duel records: {exception.Message}");
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
                Chat.WriteLine($"Unable to save Adventurer duel records: {exception.Message}");
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
            if (!_duelAccepted || _opponent == Identity.None)
                return;

            _minimumLocalDuelHealth = Math.Min(_minimumLocalDuelHealth, DynelManager.LocalPlayer.Health);

            var opponent = ResolveOpponent();
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
            var opponent = DynelManager.GetDynel(opponentIdentity) as SimpleChar
                ?? DynelManager.Players.FirstOrDefault(player => player.Identity == opponentIdentity);
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
            Chat.WriteLine($"Adventurer duel record versus {record.OpponentName}: {record.BotWins}-{record.OpponentWins}.");
        }
    }
}
