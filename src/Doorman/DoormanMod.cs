using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using DedicatedServerMod.API;
using DedicatedServerMod.Server.Player;
using DedicatedServerMod.Shared.Configuration;
using Il2CppFishNet;
using Il2CppFishNet.Connection;
using Il2CppFishNet.Transporting;
using MelonLoader;
using MelonLoader.Utils;
using Doorman.Core;

[assembly: MelonInfo(typeof(Doorman.DoormanMod), "Doorman", Doorman.DoormanMod.Version, "HumanGenome")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace Doorman
{
    /// <summary>
    /// Doorman, a server-side add-on for Schedule I dedicated servers on the S1DedicatedServers mod.
    /// Server only: it declares no client companion, so players install nothing for it.
    ///
    /// Three things, each with its own switch in UserData\Doorman.json:
    ///   idle saver          fewer frames a second while nobody is connected (ships OFF)
    ///   live feed           a small JSON status file other tools can read (ON)
    ///   invited players only  a Steam ID list checked when somebody connects (ships OFF)
    /// It never gives items or cash, never moves a player and never touches a quest.
    /// </summary>
    public sealed class DoormanMod : ServerMelonModBase
    {
        public const string Version = "1.0.1";

        private const string SettingsFileName = "Doorman.json";
        private const string FeedFileName = "Doorman.live.json";

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Stopwatch _frame = Stopwatch.StartNew();
        private readonly HashSet<int> _startedConnections = new HashSet<int>();
        private readonly Dictionary<string, double> _inviteDecided = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly List<string> _inviteSeen = new List<string>();

        private IdlePolicy _idle;
        private AddonConfig _config = AddonConfig.Defaults();
        private string _settingsPath;
        private string _feedPath;
        private DateTime _settingsStamp = DateTime.MinValue;
        private long _settingsLength = -1;

        private bool _ready;
        private bool _connectionHooked;
        private bool _idleBroken;
        private bool _wasIdle;
        private DateTime? _readyUtc;
        private DateTime? _saveReportedUtc;

        private double _nextConnectionCount;
        private double _nextSettingsCheck;
        private double _nextFeed;
        private double _nextInviteCheck;
        private double _lastFeedAt = -1d;
        private long _framesSinceFeed;
        private int _feedWriting;
        private int _feedFailures;
        private bool _feedFailureLogged;
        private bool _updateErrorLogged;

        public override void OnInitializeMelon()
        {
            _idle = new IdlePolicy(() => _clock.Elapsed.TotalSeconds);
            _settingsPath = Path.Combine(MelonEnvironment.UserDataDirectory, SettingsFileName);
            _feedPath = Path.Combine(MelonEnvironment.UserDataDirectory, FeedFileName);
            LoadSettings(true);
        }

        public override void OnLateInitializeMelon()
        {
            // The mod finds add-ons by itself. This only covers a load order where it looked before
            // this add-on existed; registering twice is ignored by the mod.
            try
            {
                ModManager.RegisterServerMod(this);
            }
            catch (Exception ex)
            {
                LoggerInstance.Warning("Could not register with the dedicated server mod: " + ex.Message);
            }
        }

        public override void OnServerStarted()
        {
            _ready = true;
            _readyUtc = DateTime.UtcNow;
            HookConnections();
            _idle.ServerReady();
            LoggerInstance.Msg("Ready. " + _config.Describe() + ".");
        }

        public override void OnServerShutdown()
        {
            _ready = false;
            _idle.ServerStopping();
            UnhookConnections();
        }

        public override void OnBeforeSave()
        {
            // a save should never run on stretched frames
            _idle.SaveStarted();
        }

        public override void OnAfterSave()
        {
            _saveReportedUtc = DateTime.UtcNow;
            _idle.SaveFinished();
        }

        public override void OnUpdate()
        {
            if (!_ready)
            {
                return;
            }

            _framesSinceFeed++;
            double now = _clock.Elapsed.TotalSeconds;

            try
            {
                if (now >= _nextSettingsCheck)
                {
                    _nextSettingsCheck = now + 5d;
                    LoadSettings(false);
                }

                if (now >= _nextConnectionCount)
                {
                    _nextConnectionCount = now + 1d;
                    CountConnections();
                }

                if (_config.InvitedOnlyEnabled && now >= _nextInviteCheck)
                {
                    _nextInviteCheck = now + 0.25d;
                    CheckInvites(now);
                }

                if (_config.LiveFeedEnabled && now >= _nextFeed)
                {
                    _nextFeed = now + _config.LiveFeedIntervalSeconds;
                    WriteFeed(now);
                }
            }
            catch (Exception ex)
            {
                if (!_updateErrorLogged)
                {
                    _updateErrorLogged = true;
                    LoggerInstance.Warning("A step failed and will be tried again quietly: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        public override void OnLateUpdate()
        {
            // The idle saver: wait out the rest of a long frame while nobody is connected.
            // Nothing in the game or the mod is changed, so there is nothing to put back.
            if (!_ready || _idleBroken)
            {
                _frame.Restart();
                return;
            }

            try
            {
                bool idle = _idle.IsIdle;

                // Two log lines per visit, not per save: a save also runs at full speed for a
                // few seconds, and saying so every time would only grow the log.
                if (idle && !_wasIdle)
                {
                    _wasIdle = true;
                    LoggerInstance.Msg("Idle saver: nobody is connected, running at " + _idle.IdleFrameRate + " frames a second.");
                }
                else if (!idle && _wasIdle && (_idle.Connections > 0 || !_idle.Enabled))
                {
                    _wasIdle = false;
                    LoggerInstance.Msg(_idle.Enabled ? "Idle saver: a connection started, back to full speed." : "Idle saver: switched off, back to full speed.");
                }

                int wait = idle ? _idle.WaitMilliseconds(_frame.Elapsed.TotalMilliseconds) : 0;
                if (wait > 0)
                {
                    Thread.Sleep(wait);
                }
            }
            catch (Exception ex)
            {
                _idleBroken = true;
                LoggerInstance.Warning("Idle saver switched itself off after a problem: " + ex.GetType().Name + ": " + ex.Message);
            }

            _frame.Restart();
        }

        // ---- settings

        private void LoadSettings(bool first)
        {
            DateTime stamp;
            long length;
            try
            {
                FileInfo info = new FileInfo(_settingsPath);
                if (!info.Exists)
                {
                    if (first)
                    {
                        LoggerInstance.Msg("No settings file (" + SettingsFileName + "): " + _config.Describe() + ".");
                        ApplySettings();
                    }
                    else if (_settingsLength != -1)
                    {
                        // the file was taken away while running: back to the safe defaults
                        _settingsLength = -1;
                        _settingsStamp = DateTime.MinValue;
                        _config = AddonConfig.Defaults();
                        ApplySettings();
                        LoggerInstance.Msg("Settings file removed: " + _config.Describe() + ".");
                    }

                    return;
                }

                stamp = info.LastWriteTimeUtc;
                length = info.Length;
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            if (!first && stamp == _settingsStamp && length == _settingsLength)
            {
                return;
            }

            string text;
            try
            {
                text = File.ReadAllText(_settingsPath);
            }
            catch (IOException)
            {
                // being replaced right now: read it on the next look
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            _settingsStamp = stamp;
            _settingsLength = length;

            AddonConfig parsed = AddonConfig.Parse(text);
            if (!parsed.Readable)
            {
                LoggerInstance.Warning("Settings not used: " + string.Join("; ", parsed.Notes) + ". Keeping: " + _config.Describe() + ".");
                if (first)
                {
                    ApplySettings();
                }

                return;
            }

            bool changed = first || !parsed.SameAs(_config);
            _config = parsed;
            ApplySettings();
            foreach (string note in parsed.Notes)
            {
                LoggerInstance.Warning("Settings: " + note + ".");
            }

            if (changed)
            {
                LoggerInstance.Msg((first ? "Settings read: " : "Settings changed: ") + _config.Describe() + ".");
            }
        }

        private void ApplySettings()
        {
            _idle.Configure(_config.IdleSaverEnabled, _config.IdleFrameRate, _config.IdleGraceSeconds);
            if (_config.IdleSaverEnabled && _ready && !_connectionHooked)
            {
                HookConnections();
            }

            if (!_config.InvitedOnlyEnabled)
            {
                _inviteDecided.Clear();
            }

            if (!_config.LiveFeedEnabled)
            {
                // a feed nobody is writing must not be left looking alive
                try
                {
                    File.Delete(_feedPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        // ---- connections (the idle saver's eyes)

        private void HookConnections()
        {
            if (_connectionHooked)
            {
                return;
            }

            try
            {
                if (InstanceFinder.ServerManager == null)
                {
                    LoggerInstance.Warning("Idle saver: the network layer is not up, so the idle saver stays off this run.");
                    _idleBroken = true;
                    return;
                }

                // The earliest sign of a joining player the game gives: the transport reports the
                // connection as started, before the Steam check and the handshake begin.
                InstanceFinder.ServerManager.OnRemoteConnectionState += new Action<NetworkConnection, RemoteConnectionStateArgs>(OnRemoteConnectionState);
                _connectionHooked = true;
            }
            catch (Exception ex)
            {
                // without this signal a join could start on slow frames, so the saver is not used at all
                _idleBroken = true;
                LoggerInstance.Warning("Idle saver: could not watch for new connections (" + ex.GetType().Name + ": " + ex.Message + "), so the idle saver stays off this run.");
            }
        }

        private void UnhookConnections()
        {
            if (!_connectionHooked)
            {
                return;
            }

            _connectionHooked = false;
            try
            {
                if (InstanceFinder.ServerManager != null)
                {
                    InstanceFinder.ServerManager.OnRemoteConnectionState -= new Action<NetworkConnection, RemoteConnectionStateArgs>(OnRemoteConnectionState);
                }
            }
            catch (Exception)
            {
            }
        }

        private void OnRemoteConnectionState(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            try
            {
                if (connection == null)
                {
                    return;
                }

                int id = connection.ClientId;

                // the server's own hidden player is not a person
                if (connection.IsLocalClient || id == 0)
                {
                    return;
                }

                if (args.ConnectionState == RemoteConnectionState.Started)
                {
                    _startedConnections.Add(id);
                    _idle.ConnectionStarted();
                }
                else if (args.ConnectionState == RemoteConnectionState.Stopped)
                {
                    _startedConnections.Remove(id);
                }
            }
            catch (Exception)
            {
                // when in doubt, full speed
                _idle.ConnectionStarted();
            }
        }

        // Once a second: a fresh count from the mod's own list, so a missed signal either way
        // cannot leave the server slow with somebody on it, or fast for ever with nobody.
        private void CountConnections()
        {
            int tracked = 0;
            PlayerManager players = S1DS.Server.Players;
            if (players != null)
            {
                tracked = players.GetConnectedPlayers().Count;
            }

            if (tracked == 0 && _startedConnections.Count > 0 && InstanceFinder.ServerManager != null)
            {
                // the mod sees nobody: drop any connection the network layer no longer has either
                List<int> gone = null;
                foreach (int id in _startedConnections)
                {
                    if (!InstanceFinder.ServerManager.Clients.ContainsKey(id))
                    {
                        (gone ?? (gone = new List<int>())).Add(id);
                    }
                }

                if (gone != null)
                {
                    foreach (int id in gone)
                    {
                        _startedConnections.Remove(id);
                    }
                }
            }

            _idle.ReportConnections(Math.Max(tracked, _startedConnections.Count));
        }

        // ---- invited players only

        private void CheckInvites(double now)
        {
            PlayerManager players = S1DS.Server.Players;
            if (players == null)
            {
                return;
            }

            IReadOnlyList<ConnectedPlayerInfo> connected = players.GetConnectedPlayers();
            _inviteSeen.Clear();
            bool serverConfirms = ServerConfig.Instance != null && ServerConfig.Instance.AuthenticationEnabled;

            for (int i = 0; i < connected.Count; i++)
            {
                ConnectedPlayerInfo player = connected[i];
                if (player == null)
                {
                    continue;
                }

                // one decision per connection: the list is checked when somebody connects,
                // and a later change to the list does not remove a player who is already in
                string key = player.ClientId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + player.ConnectTime.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _inviteSeen.Add(key);
                if (_inviteDecided.TryGetValue(key, out double decidedAt))
                {
                    // allowed players are stored as -1; a turned-away one still here after 3s is told again
                    if (decidedAt < 0d || now - decidedAt < 3d)
                    {
                        continue;
                    }
                }

                InviteVerdict verdict = InviteGate.Decide(_config, new ConnectionIdentity
                {
                    IsLoopback = player.IsLoopbackConnection,
                    ConfirmedSteamId = player.AuthenticatedSteamId,
                    ReportedSteamId = player.SteamId,
                    ServerConfirmsIdentity = serverConfirms,
                    JoinFinished = player.HasCompletedJoinFlow
                });

                if (verdict == InviteVerdict.Allow)
                {
                    _inviteDecided[key] = -1d;
                }
                else if (verdict == InviteVerdict.TurnAway)
                {
                    bool first = !_inviteDecided.ContainsKey(key);
                    _inviteDecided[key] = now;
                    players.NotifyAndDisconnectPlayer(player, "Invited players only", _config.InvitedOnlyMessage);
                    if (first)
                    {
                        LoggerInstance.Msg("Invited players only: turned away " + player.DisplayName + " (Steam ID " + (string.IsNullOrEmpty(player.TrustedUniqueId) ? "unknown" : player.TrustedUniqueId) + "), not on the list.");
                    }
                }
            }

            if (_inviteDecided.Count > _inviteSeen.Count)
            {
                List<string> stale = new List<string>();
                foreach (string key in _inviteDecided.Keys)
                {
                    if (!_inviteSeen.Contains(key))
                    {
                        stale.Add(key);
                    }
                }

                foreach (string key in stale)
                {
                    _inviteDecided.Remove(key);
                }
            }
        }

        // ---- live feed

        private void WriteFeed(double now)
        {
            FeedSnapshot snapshot = new FeedSnapshot
            {
                AddonVersion = Version,
                WrittenUtc = DateTime.UtcNow,
                ReadyUtc = _readyUtc,
                SaveReportedUtc = _saveReportedUtc,
                IdleSaverOn = _config.IdleSaverEnabled && !_idleBroken,
                IdleNow = !_idleBroken && _idle.IsIdle,
                IdleFrameRate = _config.IdleFrameRate,
                InvitedOnlyOn = _config.InvitedOnlyEnabled,
                InvitedCount = _config.InvitedSteamIds.Count
            };

            if (_lastFeedAt >= 0d && now > _lastFeedAt)
            {
                snapshot.MeasuredFrameRate = _framesSinceFeed / (now - _lastFeedAt);
            }

            _lastFeedAt = now;
            _framesSinceFeed = 0;

            ServerConfig serverConfig = ServerConfig.Instance;
            if (serverConfig != null)
            {
                snapshot.MaxPlayers = serverConfig.MaxPlayers;
                snapshot.NormalFrameRate = serverConfig.TargetFrameRate;
            }

            try
            {
                var time = S1DS.Server.GameSystems?.TimeSystem?.GetTimeInfo();
                if (time != null && time.IsAvailable)
                {
                    snapshot.ClockAvailable = true;
                    snapshot.Day = time.Day;
                    snapshot.Hour = time.Hour;
                    snapshot.Minute = time.Minute;
                }
            }
            catch (Exception)
            {
                snapshot.ClockAvailable = false;
            }

            PlayerManager players = S1DS.Server.Players;
            if (players != null)
            {
                IReadOnlyList<ConnectedPlayerInfo> connected = players.GetConnectedPlayers();
                for (int i = 0; i < connected.Count; i++)
                {
                    ConnectedPlayerInfo player = connected[i];
                    if (player == null || player.IsLoopbackConnection)
                    {
                        continue;
                    }

                    FeedPlayer entry = new FeedPlayer
                    {
                        Name = player.DisplayName ?? string.Empty,
                        SteamId = InviteGate.IsSteamId64(player.TrustedUniqueId) ? player.TrustedUniqueId : string.Empty,
                        ConnectedUtc = player.ConnectTime,
                        Joined = player.HasCompletedJoinFlow
                    };

                    try
                    {
                        var body = player.PlayerInstance;
                        if (body != null)
                        {
                            UnityEngine.Vector3 position = body.transform.position;
                            entry.HasPosition = true;
                            entry.X = position.x;
                            entry.Z = position.z;
                        }
                    }
                    catch (Exception)
                    {
                        entry.HasPosition = false;
                    }

                    snapshot.Players.Add(entry);
                }
            }

            // The disk is touched off the game thread, one write at a time. If the last write is
            // still going (a slow disk), this one is skipped: the next has newer numbers anyway.
            if (Interlocked.CompareExchange(ref _feedWriting, 1, 0) != 0)
            {
                return;
            }

            string saveFolder = null;
            try
            {
                saveFolder = ServerConfig.GetResolvedSaveGamePath();
            }
            catch (Exception)
            {
            }

            string feedPath = _feedPath;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    snapshot.SaveFilesUtc = LiveFeed.NewestSaveFileUtc(saveFolder);
                    if (LiveFeed.WriteAtomic(feedPath, LiveFeed.ToJson(snapshot)))
                    {
                        Interlocked.Exchange(ref _feedFailures, 0);
                    }
                    else
                    {
                        Interlocked.Increment(ref _feedFailures);
                    }
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _feedFailures);
                }
                finally
                {
                    Interlocked.Exchange(ref _feedWriting, 0);
                }
            });

            if (!_feedFailureLogged && Volatile.Read(ref _feedFailures) >= 5)
            {
                _feedFailureLogged = true;
                LoggerInstance.Warning("Live feed: the file could not be written five times in a row (" + FeedFileName + "). Still trying, quietly.");
            }
        }
    }
}
