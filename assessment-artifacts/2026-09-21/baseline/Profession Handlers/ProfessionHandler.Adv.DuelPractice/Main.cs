using System;
using AOSharp.Common.GameData;
using AOSharp.Core;
using AOSharp.Core.UI;

namespace ProfessionHandler.Adventurer.DuelPractice
{
    public class Main : AOPluginEntry
    {
        public override void Run()
        {
            try
            {
                if (Game.IsNewEngine)
                {
                    Chat.WriteLine("Adventurer Duel Practice does not support the new engine.");
                    return;
                }

                var localPlayer = DynelManager.LocalPlayer;
                if (localPlayer == null || localPlayer.Profession != Profession.Adventurer)
                {
                    Chat.WriteLine("Adventurer Duel Practice only loads on an Adventurer.");
                    return;
                }

                if (localPlayer.Level != 220)
                    Chat.WriteLine($"Warning: this Duel Practice profile targets level 220; current level is {localPlayer.Level}.");

                global::ProfessionHandler.Generic.Combat.ProfessionHandler.Set(
                    new AdventurerDuelPracticeHandler(PluginDirectory));
            }
            catch (Exception exception)
            {
                Chat.WriteLine(exception.Message);
            }
        }
    }
}
