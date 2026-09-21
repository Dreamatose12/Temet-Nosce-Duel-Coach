using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using AOSharp.Common.GameData;
using AOSharp.Core;
using AOSharp.Core.Inventory;
using AOSharp.Core.UI;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.ChatMessages;

namespace ProfessionHandler.Agent.DuelPractice
{
    public partial class AgentDuelPracticeHandler
    {
        private enum PracticeMode { Punchbag = 1, WorthyOpponent = 2, AiOverlord = 3 }
        private enum PracticeState { Idle, Requested, CooldownCheck, Preparing, Challenged, Accepted, Countdown, Active, Ending, Recovery }
        private PracticeState _state = PracticeState.Idle;
        private PracticeMode _mode;
        private bool _sessionAccepted;
        private bool _invitationSent;
        private Identity _invitedOpponent = Identity.None;
        private double _inviteAt;
        private double _inviteExpiresAt;
        private double _sessionEndsAt;
        private double _recoveryUntil;
        private bool _teachingPause;
        private Identity _lastReportParticipant = Identity.None;
        private string _lastCompletedReportPath;
        private readonly ConcurrentQueue<Tuple<uint, string>> _tells = new ConcurrentQueue<Tuple<uint, string>>();
        private double _nextReplyAt;
        private double _worthyDamageWindowStart;
        private double _worthyDamageWindowAmount;
        private double _worthyDamagePauseUntil;

        private void SetPracticeState(PracticeState next, string reason)
        {
            if (_state == next) return;
            _state = next;
            _recorder?.Record("state", new { state = next.ToString(), reason });
        }

        private bool OwnsPracticeService => ReferenceEquals(
            global::ProfessionHandler.Generic.Combat.ProfessionHandler.Instance, this);
        private bool PracticeEnabled => OwnsPracticeService && _settings["DuelPracticeEnabled"].AsBool()
            && _settings["Enable"].AsBool();
        private bool BurstEnabled => PracticeEnabled && _sessionAccepted && _mode == PracticeMode.AiOverlord
            && _settings["DuelBurstControlEnabled"].AsBool();

        private void InitializePracticeService()
        {
            Network.ChatMessageReceived += ReceivePracticeTell;
            Game.TeleportStarted += PracticeTeleportStarted;
            Game.OnUpdate += PracticeHeartbeat;
            Network.N3MessageReceived += RecordDuelPacket;
            Item.ItemUsed += RecordItemUsed;
        }

        private void PracticeHeartbeat(object sender, float elapsed)
        {
            if (!OwnsPracticeService) return;
            TickEquipment();
            if ((_sessionAccepted || _invitedOpponent != Identity.None)
                && (Game.IsZoning || !DynelManager.LocalPlayer.IsAlive || !PracticeEnabled))
                EndPractice("Character unavailable or handler disabled", true);
        }

        public void StopPracticeService()
        {
            Network.ChatMessageReceived -= ReceivePracticeTell;
            Game.TeleportStarted -= PracticeTeleportStarted;
            Game.OnUpdate -= PracticeHeartbeat;
            Network.N3MessageReceived -= RecordDuelPacket;
            Item.ItemUsed -= RecordItemUsed;
            Duel.Challenged -= OnDuelChallenged;
            Duel.StatusChanged -= OnDuelStatusChanged;
            if (OwnsPracticeService)
            {
                _settings["DuelPracticeEnabled"] = false;
                EndPractice("Plugin unloaded", true);
                _equipment?.Teardown();
            }
        }

        private void PracticeTeleportStarted(object sender, EventArgs args)
        {
            if (OwnsPracticeService) EndPractice("Zoning interrupted practice", true);
        }

        // Lifecycle must keep running when the generic Enable toggle is turned off.
        protected override bool ShouldRunWhenHandlerDisabled() => true;
        protected override bool ShouldRunGenericHousekeeping() => false;

        private void ReceivePracticeTell(object sender, ChatMessageBody message)
        {
            if (!OwnsPracticeService || !(message is PrivateMsgMessage tell)
                || tell.Sender == (uint)Game.ClientInst || string.IsNullOrWhiteSpace(tell.Text)
                || tell.Text.Length > 160 || _tells.Count >= 32) return;
            _tells.Enqueue(Tuple.Create(tell.Sender, tell.Text));
        }

        private void Reply(uint recipient, string text)
        {
            // Bound outbound traffic globally, including direct challenge spam.
            if (Time.AONormalTime < _nextReplyAt) return;
            _nextReplyAt = Time.AONormalTime + 1;
            Chat.SendPrivateMessage(recipient, text);
        }

        private string PracticeStatus()
        {
            if (!PracticeEnabled) return "Practice is disabled by the operator.";
            if (_sessionAccepted) return $"Practicing {_mode} with {_activeOpponentName} ({_state}).";
            if (_invitedOpponent != Identity.None)
            {
                var preparation = PreparationReason();
                return preparation == null
                    ? "A practice invitation is reserved; please wait."
                    : "Preparing before challenge: " + preparation + ".";
            }
            var reason = RecoveryReason();
            return reason == null ? "Ready. Tell practice 1, practice 2 or practice 3." : "Recovering: " + reason;
        }

        private void ProcessPracticeTell(uint sender, string text)
        {
            var command = text.Trim().TrimStart('!').Trim().ToLowerInvariant();
            if (command == "help")
            {
                Reply(sender, "Practice: 1 Punchbag (no retaliation; 5 minutes), 2 Worthy Opponent (paced fight, recovery breaks), 3 AI Overlord (burst and reactive perk timing). Tell practice 1/2/3; status = readiness; cancel = your session; report = latest report; coach <question> = Discord coaching instructions.");
                return;
            }
            if (command == "status") { Reply(sender, PracticeStatus()); return; }
            if (command == "report")
            {
                if (_lastReportParticipant.Instance != (int)sender)
                    Reply(sender, "Reports are private to the last practice participant.");
                else if (_sessionAccepted || _invitedOpponent.Instance == (int)sender)
                    Reply(sender, "Your current practice must end before its report is available.");
                else if (string.IsNullOrWhiteSpace(_lastCompletedReportPath))
                    Reply(sender, "No completed practice report is available yet.");
                else
                    Reply(sender, "Your report is complete. Ask !coach <question> in the configured Temet Nosce coaching channel.");
                return;
            }
            if (command.StartsWith("coach "))
            {
                if (_lastReportParticipant.Instance != (int)sender)
                    Reply(sender, "Coaching is private to the last practice participant.");
                else if (_sessionAccepted || _invitedOpponent.Instance == (int)sender)
                    Reply(sender, "Finish the current practice first; then ask !coach <question> in Temet Nosce.");
                else if (string.IsNullOrWhiteSpace(_lastCompletedReportPath))
                    Reply(sender, "No completed report is available for coaching yet.");
                else
                    Reply(sender, "Your question is ready for the Temet Nosce coach. Use !coach " + command.Substring(6).Trim());
                return;
            }
            if (command == "cancel")
            {
                if (_invitedOpponent.Instance == (int)sender || _opponent.Instance == (int)sender)
                {
                    EndPractice("Cancelled by participant", true);
                    Reply(sender, "Your practice session was cancelled.");
                }
                return;
            }
            if (command.StartsWith("practice ")) command = command.Substring(9).Trim();
            if (command.StartsWith("duel ")) command = command.Substring(5).Trim();
            if (!int.TryParse(command, out int mode) || mode < 1 || mode > 3) return;
            if (!PracticeEnabled || _sessionAccepted || _invitedOpponent != Identity.None)
            {
                Reply(sender, PracticeStatus());
                return;
            }
            SetPracticeState(PracticeState.CooldownCheck, "Practice request received");
            var reason = RecoveryReason();
            if (reason != null)
            {
                SetPracticeState(Time.AONormalTime < _recoveryUntil ? PracticeState.Recovery : PracticeState.Idle, reason);
                Reply(sender, "Not ready: " + reason + ". Ask status before retrying.");
                return;
            }
            var player = DynelManager.Players.FirstOrDefault(p => p.Identity.Instance == (int)sender && p.IsAlive);
            if (player == null || player.DistanceFrom(DynelManager.LocalPlayer) > 20)
            {
                Reply(sender, "Come within 20 metres of me to request practice.");
                return;
            }
            _mode = (PracticeMode)mode;
            _invitedOpponent = player.Identity;
            _inviteAt = Time.AONormalTime + DuelAcceptanceDelaySeconds;
            _inviteExpiresAt = _inviteAt + 60;
            _invitationSent = false;
            SetPracticeState(PracticeState.Preparing, "Participant reserved; self-buff preparation enabled");
            Reply(sender, $"{_mode} reserved. I will challenge you after preparation and nano recharge are complete. Accept the invitation; cancel ends your session.");
        }

        private string RecoveryReason()
        {
            try
            {
                if (Game.IsZoning) return "zoning";
                if (_gearRestorePending || (_equipment?.Busy ?? false)) return "equipment swap or return preset pending";
                if (Time.AONormalTime < _recoveryUntil) return "post-duel settling period";
                var lp = DynelManager.LocalPlayer;
                if (!lp.IsAlive || lp.HealthPercent < 99 || lp.NanoPercent < 99) return "health/nano below 99%";
                if (lp.IsAttacking || lp.IsAttackPending) return "combat is still active";
                if (Spell.HasPendingCast || Item.HasPendingUse) return "an action is pending";
                // Read actual local skill locks, including item/First Aid cooldowns.
                var locks = lp.Cooldowns.Where(c => c.Value.Remaining > 0).Select(c => c.Key).ToArray();
                if (locks.Length > 0) return "skill locks: " + string.Join(", ", locks.Take(4));
                var perk = PerkAction.List.FirstOrDefault(p => p.IsPending || p.IsExecuting || !p.IsAvailable);
                if (perk != null) return "perk unavailable: " + perk.Name;
                if (lp.SpecialAttacks.Any(s => !s.IsAvailable())) return "weapon special cooldown";
                if (SkillLock.Any(s => s.Item2 > Time.AONormalTime)) return "tracked item cooldown";
                if (_actionQueue.Count > 0) return "queued actions";
                return null;
            }
            catch (Exception e) { return "readiness unavailable: " + e.GetType().Name; }
        }

        private bool IsPracticePreparationActive
            => PracticeEnabled && !_sessionAccepted && _invitedOpponent != Identity.None && !_invitationSent;

        private string PreparationReason()
        {
            try
            {
                if (!IsPracticePreparationActive)
                    return null;

                if (Spell.HasPendingCast)
                    return "nano casting";

                if (!IsConfiguredFalseProfessionReady(out var falseProfessionReason))
                    return falseProfessionReason;

                return null;
            }
            catch (Exception e) { return "preparation unavailable: " + e.GetType().Name; }
        }

        private bool IsConfiguredFalseProfessionReady(out string reason)
        {
            reason = null;

            if (!_settings["CastFP"].AsBool())
                return true;

            if (DynelManager.LocalPlayer.Buffs.Contains(NanoLine.FalseProfession))
                return true;

            var selectedSpellId = NextConfiguredFalseProfessionSpellId();
            if (selectedSpellId == 0)
                return true;

            if (!Spell.Find(selectedSpellId, out Spell spell))
            {
                reason = "selected False Profession nano is unavailable";
                return false;
            }

            if (!CanCast(spell))
            {
                reason = $"waiting for {spell.Name}";
                return false;
            }

            reason = $"casting {spell.Name}";
            return false;
        }

        private int NextConfiguredFalseProfessionSpellId()
        {
            var first = _settings["FalseProfSelection1"].AsInt32();
            if (first != 0 && _settings["LastFP"].AsInt32() != first)
                return first;

            var second = _settings["FalseProfSelection2"].AsInt32();
            if (second != 0 && _settings["LastFP"].AsInt32() != second)
                return second;

            return 0;
        }

        private bool UpdatePracticeService()
        {
            if (!OwnsPracticeService) return false;
            if (_state == PracticeState.Recovery && Time.AONormalTime >= _recoveryUntil)
                SetPracticeState(PracticeState.Idle, "Recovery complete");
            if (!PracticeEnabled && (_sessionAccepted || _invitedOpponent != Identity.None))
                EndPractice("Practice disabled", true);
            if (Time.AONormalTime >= _nextReplyAt && _tells.TryDequeue(out var tell))
                ProcessPracticeTell(tell.Item1, tell.Item2);
            if (!PracticeEnabled) return false;
            if (_invitedOpponent != Identity.None && !_sessionAccepted)
            {
                if (Time.AONormalTime >= _inviteExpiresAt)
                    EndPractice("Invitation expired", true);
                else if (!_invitationSent && Time.AONormalTime >= _inviteAt)
                {
                    var player = DynelManager.Players.FirstOrDefault(p => p.Identity == _invitedOpponent && p.IsAlive);
                    var reason = RecoveryReason();
                    var preparation = PreparationReason();
                    if (reason != null || preparation != null || player == null || player.DistanceFrom(DynelManager.LocalPlayer) > 20)
                    {
                        if ((reason != null || preparation != null)
                            && player != null
                            && player.DistanceFrom(DynelManager.LocalPlayer) <= 20)
                        {
                            SetPracticeState(PracticeState.Preparing, preparation ?? reason);
                            return true;
                        }

                        Reply((uint)_invitedOpponent.Instance, "Invitation cancelled: " + (reason ?? preparation ?? "player out of range"));
                        EndPractice("Readiness changed", false);
                    }
                    else
                    {
                        _invitationSent = true;
                        SetPracticeState(PracticeState.Challenged, "Challenge sent after readiness recheck");
                        Duel.Challenge(_invitedOpponent);
                    }
                }
            }
            if (!_sessionAccepted) return false;
            if (_state == PracticeState.Countdown && Time.AONormalTime >= _attackReadyAt)
                SetPracticeState(PracticeState.Active, "Duel countdown complete");
            if (Time.AONormalTime >= _sessionEndsAt)
            {
                Reply((uint)_opponent.Instance, "Practice time limit reached.");
                EndPractice("Time limit", true);
                return false;
            }
            var opponent = ResolveOpponent();
            if (opponent == null)
            {
                EndPractice("Opponent unavailable", true);
                return false;
            }
            _recorder?.Sample(DynelManager.LocalPlayer, opponent, Time.AONormalTime);
            UpdateAdaptiveEquipment(opponent);
            if (_recorder?.Error != null)
            {
                Chat.WriteLine("Duel recording failed: " + _recorder.Error);
                _recorder.Finish("Recorder failed");
                _recorder = null;
            }
            if (_mode == PracticeMode.Punchbag)
            {
                _actionQueue.Clear();
                if (DynelManager.LocalPlayer.IsAttacking || DynelManager.LocalPlayer.IsAttackPending)
                    DynelManager.LocalPlayer.StopAttack(false);
                foreach (var pet in DynelManager.LocalPlayer.Pets) pet?.Follow();
            }
            else if (_mode == PracticeMode.WorthyOpponent)
            {
                if (Time.AONormalTime < _worthyDamagePauseUntil)
                {
                    RecordDecision("Worthy damage ceiling pause; controlled recovery window");
                    _actionQueue.Clear();
                    DynelManager.LocalPlayer.StopAttack(false);
                    foreach (var pet in DynelManager.LocalPlayer.Pets) pet?.Follow();
                    return false;
                }
                // Hysteresis gives the learner a real recovery window rather than oscillation.
                if (!_teachingPause && opponent.HealthPercent <= 25) _teachingPause = true;
                else if (_teachingPause && opponent.HealthPercent >= 60) _teachingPause = false;
                if (_teachingPause)
                {
                    RecordDecision("Teaching pause: learner below health threshold; resumes at 60%");
                    _actionQueue.Clear();
                    DynelManager.LocalPlayer.StopAttack(false);
                    foreach (var pet in DynelManager.LocalPlayer.Pets) pet?.Follow();
                    return false;
                }
            }
            return true;
        }

        private void AcceptReservedPractice(Identity opponent)
        {
            if (_sessionAccepted) return; // Duplicate Accepted must not reset timers.
            if (!_invitationSent || opponent != _invitedOpponent || !PracticeEnabled
                || Time.AONormalTime >= _inviteExpiresAt || RecoveryReason() != null)
            {
                EndPractice("Unreserved or no-longer-ready acceptance", true);
                return;
            }
            _sessionAccepted = true;
            _worthyDamageWindowStart = Time.AONormalTime;
            _worthyDamageWindowAmount = 0;
            _worthyDamagePauseUntil = 0;
            SetPracticeState(PracticeState.Accepted, "Duel acceptance received");
            _lastReportParticipant = opponent;
            _teachingPause = false;
            if (_mode == PracticeMode.AiOverlord) _settings["DuelBurstControlEnabled"] = true;
            ArmDuelAttack(opponent);
            _recorder = new DuelRecorder(_reportDirectory, _mode.ToString(), Game.ClientInst, opponent.Instance, _activeOpponentName);
            SetPracticeState(PracticeState.Countdown, "Accepted; waiting for duel countdown");
            BeginAdaptiveEquipment();
            // The five-minute attempt begins after the normal duel countdown.
            _sessionEndsAt = _attackReadyAt + (_mode == PracticeMode.Punchbag ? 300 : 900);
            _invitedOpponent = Identity.None;
            _actionQueue.Clear();
            Reply((uint)opponent.Instance, $"{_mode} accepted. Practice starts after the duel countdown.");
        }

        private void EndPractice(string reason, bool stopDuel)
        {
            bool hadSession = _sessionAccepted;
            bool hadInvitation = _invitationSent;
            EndAdaptiveEquipment();
            if (_recorder != null)
            {
                var observedOpponent = DynelManager.Players.FirstOrDefault(p => p.Identity == _opponent);
                if (!DynelManager.LocalPlayer.IsAlive || (observedOpponent != null && !observedOpponent.IsAlive))
                {
                    string observedOutcome = !DynelManager.LocalPlayer.IsAlive && (observedOpponent == null || !observedOpponent.IsAlive)
                        ? "unknown" : (!DynelManager.LocalPlayer.IsAlive ? "opponent_won" : "bot_won");
                    _recorder.Record("outcomeObservation", new
                    {
                        outcome = observedOutcome,
                        evidence = "client_alive_state",
                        localAlive = DynelManager.LocalPlayer.IsAlive,
                        opponentAlive = observedOpponent?.IsAlive
                    });
                }
                SetPracticeState(PracticeState.Ending, reason);
                _recorder.Finish(reason);
                _lastCompletedReportPath = _recorder.FilePath;
            }
            _recorder = null;
            _sessionAccepted = false;
            _invitationSent = false;
            _invitedOpponent = Identity.None;
            _opponent = Identity.None;
            _sessionEndsAt = 0;
            _teachingPause = false;
            _actionQueue.Clear();
            ClearPendingDuelAcceptance();
            ClearAttackSchedule();
            StopBurstSequence(reason, false);
            if (hadSession) _recoveryUntil = Time.AONormalTime + 5;
            SetPracticeState(hadSession ? PracticeState.Recovery : PracticeState.Idle, reason);
            if (hadSession) DynelManager.LocalPlayer.StopAttack(false);
            if (stopDuel && (hadSession || hadInvitation)) Duel.Stop();
            Chat.WriteLine("Practice: " + reason);
        }

        protected override bool ShouldProcessActionTarget(SimpleChar fightingTarget, SimpleChar actionTarget)
        {
            if (IsPracticePreparationActive)
                return actionTarget != null && actionTarget.Identity == DynelManager.LocalPlayer.Identity;

            if (!PracticeEnabled || !_sessionAccepted || _mode == PracticeMode.Punchbag || _teachingPause) return false;
            if (actionTarget == null || actionTarget.Identity == DynelManager.LocalPlayer.Identity) return true;
            return CanProcessPlayerTarget(actionTarget);
        }

        protected override bool ShouldProcessSpellAction(Spell spell, SimpleChar fightingTarget)
        {
            if (IsPracticePreparationActive)
                return fightingTarget == null;

            bool allowed = PracticeEnabled && _sessionAccepted && _mode != PracticeMode.Punchbag;
            if (allowed) _recorder?.Record("actionRequested", new { action = spell?.ToString(), kind = "spell" });
            return allowed;
        }

        protected override bool ShouldProcessPerkActionTarget(PerkAction perk, SimpleChar fightingTarget, SimpleChar target)
        {
            if (!ShouldProcessActionTarget(fightingTarget, target)) return false;
            if (_mode == PracticeMode.WorthyOpponent && target?.Identity == _opponent) return false;
            // Only observed buffs are used. Absence is not proof that every defense is known.
            if (_mode == PracticeMode.AiOverlord && target?.Identity == _opponent
                && HasObservedEvasionDefense(target))
            {
                var decision = MakeDecision(DuelDecisionAction.HoldPerk, target,
                    "Dance of Fools observed; defer opponent-targeted perk", "observed buff");
                if (AuthorizeDecision(decision, target))
                {
                    RecordDecision(decision);
                    return false;
                }
            }
            return true;
        }

        private static bool HasObservedEvasionDefense(SimpleChar target)
            => target != null && target.Buffs.Any(b => string.Equals(b.Name, "Dance of Fools", StringComparison.OrdinalIgnoreCase)
                && b.RemainingTime > 0);

        protected override bool ShouldProcessExplicitPerkAction(PerkAction perk, SimpleChar target)
            => CanProcessPlayerTarget(target) && _mode == PracticeMode.AiOverlord;
        protected override bool ShouldProcessExplicitItemAction(Item item, SimpleChar target)
            => CanProcessPlayerTarget(target) && _mode == PracticeMode.AiOverlord;
    }
}
