using System;

namespace Doorman.Core
{
    /// <summary>
    /// Decides, frame by frame, whether the server may run slowly because nobody is there.
    ///
    /// The rule is deliberately one-sided: anything that could be a person (a connection that has
    /// only just started, a player still in the Steam check, a save in progress) means full speed
    /// at once, and slow running only begins after a quiet stretch with no connections at all.
    /// The add-on never changes the game's own frame rate setting; it only waits a little longer
    /// at the end of each frame while this says "idle", so "putting it back" is simply not waiting.
    /// </summary>
    public sealed class IdlePolicy
    {
        private readonly Func<double> _now;
        private bool _enabled;
        private int _idleFrameRate = AddonConfig.DefaultIdleFrameRate;
        private int _graceSeconds = AddonConfig.DefaultIdleGraceSeconds;
        private bool _ready;
        private int _connections;
        private double _fullSpeedUntil;
        private double _saveUntil;

        /// <param name="nowSeconds">A steady clock in seconds (a stopwatch, not the wall clock).</param>
        public IdlePolicy(Func<double> nowSeconds)
        {
            _now = nowSeconds ?? throw new ArgumentNullException(nameof(nowSeconds));
        }

        public bool Enabled => _enabled;

        public int IdleFrameRate => _idleFrameRate;

        /// <summary>Connections that are not the server's own hidden player, as last reported.</summary>
        public int Connections => _connections;

        public void Configure(bool enabled, int idleFrameRate, int graceSeconds)
        {
            bool turnedOn = enabled && !_enabled;
            _enabled = enabled;
            _idleFrameRate = Math.Min(AddonConfig.MaxIdleFrameRate, Math.Max(AddonConfig.MinIdleFrameRate, idleFrameRate));
            _graceSeconds = Math.Max(0, graceSeconds);
            if (turnedOn)
            {
                // switched on while running: wait a full quiet stretch before the first slow frame
                HoldFullSpeed(_graceSeconds);
            }
        }

        /// <summary>The server has finished starting. Slow running may begin after the quiet stretch.</summary>
        public void ServerReady()
        {
            _ready = true;
            HoldFullSpeed(_graceSeconds);
        }

        public void ServerStopping()
        {
            _ready = false;
        }

        /// <summary>A connection has just started (the earliest sign of a joining player): full speed now.</summary>
        public void ConnectionStarted()
        {
            if (_connections < 1)
            {
                _connections = 1;
            }

            HoldFullSpeed(_graceSeconds);
        }

        /// <summary>The current number of connections, from a fresh count. Above zero always means full speed.</summary>
        public void ReportConnections(int count)
        {
            if (count < 0)
            {
                count = 0;
            }

            if (count > 0 || _connections > 0)
            {
                // somebody is here, or the last one has only just gone: restart the quiet stretch
                HoldFullSpeed(_graceSeconds);
            }

            _connections = count;
        }

        /// <summary>How long a save may hold full speed if nothing ever says it finished.</summary>
        public const double SaveLongestSeconds = 60d;

        /// <summary>Full speed for this long after a save is reported finished (files are still settling).</summary>
        public const double SaveAfterSeconds = 10d;

        /// <summary>A save is starting: it never runs on stretched frames.</summary>
        public void SaveStarted()
        {
            _saveUntil = _now() + SaveLongestSeconds;
        }

        /// <summary>The save is reported finished: a short full-speed tail, then idle may resume.</summary>
        public void SaveFinished()
        {
            _saveUntil = _now() + SaveAfterSeconds;
        }

        /// <summary>True when this frame may be stretched.</summary>
        public bool IsIdle
        {
            get
            {
                if (!_enabled || !_ready || _connections != 0)
                {
                    return false;
                }

                double now = _now();
                return now >= _fullSpeedUntil && now >= _saveUntil;
            }
        }

        /// <summary>
        /// How long to wait at the end of a frame that has already taken <paramref name="frameMilliseconds"/>.
        /// Zero whenever the server is not idle.
        /// </summary>
        public int WaitMilliseconds(double frameMilliseconds)
        {
            if (!IsIdle)
            {
                return 0;
            }

            double target = 1000d / _idleFrameRate;
            double remaining = target - frameMilliseconds;
            return remaining > 0d ? (int)Math.Ceiling(remaining) : 0;
        }

        private void HoldFullSpeed(double seconds)
        {
            double until = _now() + Math.Max(0d, seconds);
            if (until > _fullSpeedUntil)
            {
                _fullSpeedUntil = until;
            }
        }
    }
}
