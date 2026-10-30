using System;
using System.Collections.Generic;
using System.Globalization;

namespace Doorman.Core
{
    /// <summary>What to do about one connection after looking at it.</summary>
    public enum InviteAction
    {
        /// <summary>Nothing: let in, or still in the Steam check.</summary>
        Nothing,

        /// <summary>Turned away for the first time: show the plain message, then disconnect.</summary>
        TellAndDisconnect,

        /// <summary>Told already and still connected: close the connection directly, no message.</summary>
        DisconnectNow
    }

    /// <summary>
    /// The invited list is checked once per connection: a later change to the list does not remove
    /// a player who is already in. This remembers each connection's answer. A connection that was
    /// turned away and is still there <see cref="RetrySeconds"/> later is closed directly, because
    /// the first try goes through the mod's message and the mod's timer, and either can fail.
    /// Kept free of game code so it can be tested without a game, like <see cref="InviteGate"/>.
    /// </summary>
    public sealed class InviteBook
    {
        /// <summary>How long a turned-away connection gets to go before it is closed directly.</summary>
        public const double RetrySeconds = 3d;

        private sealed class Entry
        {
            public bool Allowed;
            public double LastTry;
            public int Tries;
        }

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>One connection: a client number can come back later, the same number at the same time cannot.</summary>
        public static string Key(int clientId, DateTime connected)
        {
            return clientId.ToString(CultureInfo.InvariantCulture) + ":" + connected.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Connections remembered, for the tests.</summary>
        public int Count => _entries.Count;

        /// <summary>Forget everything (the switch went off, so the next switch-on checks everybody).</summary>
        public void Clear()
        {
            _entries.Clear();
            _seen.Clear();
        }

        /// <summary>A new look at the connected players starts.</summary>
        public void BeginRound()
        {
            _seen.Clear();
        }

        /// <summary>
        /// Notes this connection as still here. True when it needs a decision now; false when it was
        /// let in already, or was turned away less than <see cref="RetrySeconds"/> ago.
        /// </summary>
        public bool NeedsDecision(string key, double now)
        {
            _seen.Add(key);
            if (!_entries.TryGetValue(key, out Entry entry))
            {
                return true;
            }

            return !entry.Allowed && now - entry.LastTry >= RetrySeconds;
        }

        /// <summary>Records the decision and says what to do about it.</summary>
        public InviteAction Record(string key, InviteVerdict verdict, double now)
        {
            if (verdict == InviteVerdict.Wait)
            {
                return InviteAction.Nothing;
            }

            if (!_entries.TryGetValue(key, out Entry entry))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            if (verdict == InviteVerdict.Allow)
            {
                entry.Allowed = true;
                return InviteAction.Nothing;
            }

            entry.Allowed = false;
            entry.LastTry = now;
            entry.Tries++;
            return entry.Tries == 1 ? InviteAction.TellAndDisconnect : InviteAction.DisconnectNow;
        }

        /// <summary>Times this connection has been turned away so far.</summary>
        public int Tries(string key)
        {
            return _entries.TryGetValue(key, out Entry entry) ? entry.Tries : 0;
        }

        /// <summary>The look is over: forget every connection it did not see.</summary>
        public void EndRound()
        {
            if (_entries.Count == 0)
            {
                return;
            }

            List<string> gone = null;
            foreach (string key in _entries.Keys)
            {
                if (!_seen.Contains(key))
                {
                    (gone ?? (gone = new List<string>())).Add(key);
                }
            }

            if (gone != null)
            {
                foreach (string key in gone)
                {
                    _entries.Remove(key);
                }
            }
        }
    }
}
