namespace Doorman.Core
{
    public enum InviteVerdict
    {
        /// <summary>Let the player in.</summary>
        Allow,

        /// <summary>Steam has not confirmed who this is yet. Ask again shortly.</summary>
        Wait,

        /// <summary>Not on the invited list: disconnect with the plain reason.</summary>
        TurnAway
    }

    /// <summary>What the server knows about one connection at the moment of asking.</summary>
    public struct ConnectionIdentity
    {
        /// <summary>The server's own hidden player, never a real person.</summary>
        public bool IsLoopback;

        /// <summary>The Steam ID that Steam itself confirmed (empty until it has).</summary>
        public string ConfirmedSteamId;

        /// <summary>The Steam ID the player's own game reported. Not proof.</summary>
        public string ReportedSteamId;

        /// <summary>True when this server makes Steam confirm every player (the mod's Steam authentication is on).</summary>
        public bool ServerConfirmsIdentity;

        /// <summary>True once the mod has finished letting the player in.</summary>
        public bool JoinFinished;
    }

    /// <summary>
    /// The one decision behind "invited players only". Kept free of game code so it can be tested
    /// without a game: nobody may join the test server, so this function is where the proof is.
    /// </summary>
    public static class InviteGate
    {
        /// <summary>A Steam ID as the mod writes it: 17 digits starting 7656119.</summary>
        public static bool IsSteamId64(string value)
        {
            if (value == null || value.Length != 17 || !value.StartsWith("7656119", System.StringComparison.Ordinal))
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] < '0' || value[i] > '9')
                {
                    return false;
                }
            }

            return true;
        }

        public static InviteVerdict Decide(AddonConfig config, ConnectionIdentity who)
        {
            if (config == null || !config.InvitedOnlyEnabled)
            {
                return InviteVerdict.Allow;
            }

            if (who.IsLoopback)
            {
                return InviteVerdict.Allow;
            }

            // Steam's own word comes first, and is the only thing trusted when the server asks for it.
            if (IsSteamId64(who.ConfirmedSteamId))
            {
                return config.InvitedSteamIds.Contains(who.ConfirmedSteamId) ? InviteVerdict.Allow : InviteVerdict.TurnAway;
            }

            if (who.ServerConfirmsIdentity)
            {
                // Still in the Steam check. The mod ends that itself if it takes too long.
                // A player who is somehow all the way in with no confirmed ID is not let stay.
                return who.JoinFinished ? InviteVerdict.TurnAway : InviteVerdict.Wait;
            }

            // A server that does not ask Steam: the reported ID is all there is (and a player can fake it).
            if (IsSteamId64(who.ReportedSteamId))
            {
                return config.InvitedSteamIds.Contains(who.ReportedSteamId) ? InviteVerdict.Allow : InviteVerdict.TurnAway;
            }

            return who.JoinFinished ? InviteVerdict.TurnAway : InviteVerdict.Wait;
        }
    }
}
