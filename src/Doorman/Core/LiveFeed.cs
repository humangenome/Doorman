using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Doorman.Core
{
    /// <summary>One connected player as the feed shows it.</summary>
    public sealed class FeedPlayer
    {
        public string Name = string.Empty;
        public string SteamId = string.Empty;
        public DateTime ConnectedUtc;

        /// <summary>False while the player is still being let in (Steam check, loading).</summary>
        public bool Joined;

        public bool HasPosition;
        public float X;
        public float Z;
    }

    /// <summary>Everything one write of the feed file holds. Filled on the game thread, written off it.</summary>
    public sealed class FeedSnapshot
    {
        public string AddonVersion = string.Empty;
        public DateTime WrittenUtc;
        public DateTime? ReadyUtc;

        public bool ClockAvailable;
        public int Day;
        public int Hour;
        public int Minute;

        /// <summary>When the mod last told add-ons a save had finished, this run. Null until it has.</summary>
        public DateTime? SaveReportedUtc;

        /// <summary>The newest write time among the save folder's own files. Null when there is no save yet.</summary>
        public DateTime? SaveFilesUtc;

        public int MaxPlayers;
        public List<FeedPlayer> Players = new List<FeedPlayer>();

        public bool IdleSaverOn;
        public bool IdleNow;
        public int IdleFrameRate;
        public int NormalFrameRate;

        /// <summary>Frames a second actually counted since the last write. Negative when not measured yet.</summary>
        public double MeasuredFrameRate = -1d;

        public bool InvitedOnlyOn;

        /// <summary>
        /// True while invited players only is on AND its check is running. False when it is off, or
        /// when the check keeps failing or has nothing to look at (the log says why): a reader must
        /// not trust "on" alone to mean players are being checked.
        /// </summary>
        public bool InvitedChecking;

        public int InvitedCount;
    }

    public static class LiveFeed
    {
        public const int FormatVersion = 1;

        /// <summary>The feed file's text. Small, one object, no growth: each write replaces the last.</summary>
        public static string ToJson(FeedSnapshot s)
        {
            using (MemoryStream stream = new MemoryStream(1024))
            {
                using (Utf8JsonWriter w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
                {
                    w.WriteStartObject();
                    w.WriteNumber("format", FormatVersion);
                    w.WriteString("addon", s.AddonVersion ?? string.Empty);
                    w.WriteString("written", Iso(s.WrittenUtc));
                    w.WriteNumber("writtenUnix", ToUnix(s.WrittenUtc));
                    if (s.ReadyUtc.HasValue)
                    {
                        w.WriteString("ready", Iso(s.ReadyUtc.Value));
                    }
                    else
                    {
                        w.WriteNull("ready");
                    }

                    w.WriteStartObject("clock");
                    w.WriteBoolean("available", s.ClockAvailable);
                    if (s.ClockAvailable)
                    {
                        w.WriteNumber("day", s.Day);
                        w.WriteNumber("hour", s.Hour);
                        w.WriteNumber("minute", s.Minute);
                        w.WriteString("time", s.Hour.ToString("00", CultureInfo.InvariantCulture) + ":" + s.Minute.ToString("00", CultureInfo.InvariantCulture));
                    }

                    w.WriteEndObject();

                    w.WriteStartObject("lastSave");
                    WriteTime(w, "files", s.SaveFilesUtc);
                    WriteTime(w, "reported", s.SaveReportedUtc);
                    w.WriteEndObject();

                    w.WriteNumber("playerCount", s.Players == null ? 0 : s.Players.Count);
                    w.WriteNumber("maxPlayers", s.MaxPlayers);
                    w.WriteStartArray("players");
                    if (s.Players != null)
                    {
                        foreach (FeedPlayer p in s.Players)
                        {
                            w.WriteStartObject();
                            w.WriteString("name", p.Name ?? string.Empty);
                            w.WriteString("steamId", p.SteamId ?? string.Empty);
                            w.WriteString("connected", Iso(p.ConnectedUtc));
                            w.WriteNumber("seconds", Math.Max(0L, (long)(s.WrittenUtc - p.ConnectedUtc).TotalSeconds));
                            w.WriteBoolean("joined", p.Joined);
                            if (p.HasPosition && IsFinite(p.X) && IsFinite(p.Z))
                            {
                                w.WriteNumber("x", Math.Round(p.X, 1));
                                w.WriteNumber("z", Math.Round(p.Z, 1));
                            }
                            else
                            {
                                w.WriteNull("x");
                                w.WriteNull("z");
                            }

                            w.WriteEndObject();
                        }
                    }

                    w.WriteEndArray();

                    w.WriteStartObject("frameRate");
                    w.WriteBoolean("idleSaver", s.IdleSaverOn);
                    w.WriteBoolean("idleNow", s.IdleNow);
                    w.WriteNumber("idleLimit", s.IdleFrameRate);
                    w.WriteNumber("normalLimit", s.NormalFrameRate);
                    if (s.MeasuredFrameRate >= 0d && !double.IsNaN(s.MeasuredFrameRate) && !double.IsInfinity(s.MeasuredFrameRate))
                    {
                        w.WriteNumber("measured", Math.Round(s.MeasuredFrameRate, 1));
                    }
                    else
                    {
                        w.WriteNull("measured");
                    }

                    w.WriteEndObject();

                    w.WriteStartObject("invitedOnly");
                    w.WriteBoolean("on", s.InvitedOnlyOn);
                    w.WriteBoolean("checking", s.InvitedOnlyOn && s.InvitedChecking);
                    w.WriteNumber("listed", s.InvitedCount);
                    w.WriteEndObject();

                    w.WriteEndObject();
                }

                return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
            }
        }

        /// <summary>
        /// Writes the text to a temp file beside the target and renames it over the target, so a
        /// reader sees the old file or the new one and never half of either. Returns false (and
        /// leaves the old file) when the disk or a reader gets in the way; the next write tries again.
        /// </summary>
        public static bool WriteAtomic(string path, string text)
        {
            string temp = path + ".tmp";
            try
            {
                File.WriteAllText(temp, text, new UTF8Encoding(false));
                File.Move(temp, path, true);
                return true;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return false;
        }

        /// <summary>The newest write time among the save folder's own top-level files, or null.</summary>
        public static DateTime? NewestSaveFileUtc(string saveFolder)
        {
            try
            {
                if (string.IsNullOrEmpty(saveFolder) || !Directory.Exists(saveFolder))
                {
                    return null;
                }

                DateTime newest = DateTime.MinValue;
                foreach (string file in Directory.EnumerateFiles(saveFolder, "*.json", SearchOption.TopDirectoryOnly))
                {
                    DateTime written = File.GetLastWriteTimeUtc(file);
                    if (written > newest)
                    {
                        newest = written;
                    }
                }

                return newest == DateTime.MinValue ? (DateTime?)null : newest;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void WriteTime(Utf8JsonWriter w, string name, DateTime? value)
        {
            if (value.HasValue)
            {
                w.WriteString(name, Iso(value.Value));
            }
            else
            {
                w.WriteNull(name);
            }
        }

        private static string Iso(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        private static long ToUnix(DateTime utc)
        {
            return new DateTimeOffset(utc.ToUniversalTime()).ToUnixTimeSeconds();
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
