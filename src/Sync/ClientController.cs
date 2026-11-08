using System;
using CupheadOnline.Net;
using Rewired;

namespace CupheadOnline.Sync
{
    internal static class ClientController
    {
        public static void EnsureAssigned(Player player)
        {
            if (!Session.IsClient || player.controllers.joystickCount != 0) return;
            // PlayerManager is suspended on the mirror. Preserve existing assignments; if the user joined from
            // the title before assigning a pad, attach the controller they actually press and load their saved map.
            foreach (Joystick joystick in ReInput.controllers.Joysticks)
            {
                if (!joystick.GetAnyButton()) continue;
                player.controllers.AddController(joystick, true);
                try
                {
                    if (ReInput.userDataStore != null) ReInput.userDataStore.LoadControllerData(player.id, ControllerType.Joystick, joystick.id);
                }
                catch (Exception ex) { Plugin.Log.LogWarning("Could not load local controller mapping: " + ex.Message); }
                break;
            }
        }
    }
}
