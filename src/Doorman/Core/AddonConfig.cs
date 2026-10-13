using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Doorman.Core
{
    /// <summary>
    /// The add-on's own settings, read from UserData\Doorman.json.
    /// Edit it by hand or have your hosting tools write it; a change is picked up within 5 seconds.
    /// Everything that could get in a player's way is OFF unless the file says "enabled": true.
    /// </summary>
    public sealed class AddonConfig
    {
        public const int DefaultIdleFrameRate = 10;
        public const int MinIdleFrameRate = 2;
        public const int MaxIdleFrameRate = 60;
        public const int DefaultIdleGraceSeconds = 15;
        public const int DefaultFeedIntervalSeconds = 5;
        public const int MinFeedIntervalSeconds = 2;
        public const int MaxFeedIntervalSeconds = 60;
        public const int MaxInvited = 500;
        public const string DefaultInvitedOnlyMessage =
            "This server is for invited players only. Ask the owner to add your Steam ID to the invited list.";

        /// <summary>Lower the frame rate while nobody is connected. Ships OFF.</summary>
        public bool IdleSaverEnabled { get; private set; }

        /// <summary>Frames a second while nobody is connected.</summary>
        public int IdleFrameRate { get; private set; } = DefaultIdleFrameRate;

        /// <summary>Seconds to stay at full speed after the server is ready, or after the last player left.</summary>
        public int IdleGraceSeconds { get; private set; } = DefaultIdleGraceSeconds;

        /// <summary>Write the live feed file. Safe for everyone, so it is ON unless switched off.</summary>
        public bool LiveFeedEnabled { get; private set; } = true;

        public int LiveFeedIntervalSeconds { get; private set; } = DefaultFeedIntervalSeconds;

        /// <summary>Turn away anybody whose Steam ID is not on the list. Ships OFF.</summary>
        public bool InvitedOnlyEnabled { get; private set; }

        public HashSet<string> InvitedSteamIds { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

        public string InvitedOnlyMessage { get; private set; } = DefaultInvitedOnlyMessage;

        /// <summary>Plain notes about anything in the file that was ignored or corrected.</summary>
        public List<string> Notes { get; } = new List<string>();

        /// <summary>False when the text was not a settings file at all (the caller keeps what it had).</summary>
        public bool Readable { get; private set; } = true;

        public static AddonConfig Defaults()
        {
            return new AddonConfig();
        }

        /// <summary>
        /// Reads the settings text. Never throws. Text that is not JSON gives the defaults with
        /// <see cref="Readable"/> false, so a caller that already has good settings can keep them.
        /// </summary>
        public static AddonConfig Parse(string json)
        {
            AddonConfig config = new AddonConfig();
            if (string.IsNullOrWhiteSpace(json))
            {
                config.Readable = false;
                config.Notes.Add("the settings file is empty");
                return config;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            }
            catch (JsonException)
            {
                config.Readable = false;
                config.Notes.Add("the settings file is not valid JSON");
                return config;
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    config.Readable = false;
                    config.Notes.Add("the settings file is not a JSON object");
                    return config;
                }

                if (TryObject(root, "idleSaver", out JsonElement idle))
                {
                    config.IdleSaverEnabled = ReadSwitch(idle, "enabled", false);
                    config.IdleFrameRate = ReadInt(idle, "frameRate", DefaultIdleFrameRate, MinIdleFrameRate, MaxIdleFrameRate, "idleSaver.frameRate", config.Notes);
                    config.IdleGraceSeconds = ReadInt(idle, "graceSeconds", DefaultIdleGraceSeconds, 0, 600, "idleSaver.graceSeconds", config.Notes);
                }

                if (TryObject(root, "liveFeed", out JsonElement feed))
                {
                    config.LiveFeedEnabled = ReadSwitch(feed, "enabled", true);
                    config.LiveFeedIntervalSeconds = ReadInt(feed, "intervalSeconds", DefaultFeedIntervalSeconds, MinFeedIntervalSeconds, MaxFeedIntervalSeconds, "liveFeed.intervalSeconds", config.Notes);
                }

                if (TryObject(root, "invitedOnly", out JsonElement invited))
                {
                    config.InvitedOnlyEnabled = ReadSwitch(invited, "enabled", false);
                    if (invited.TryGetProperty("steamIds", out JsonElement ids) && ids.ValueKind == JsonValueKind.Array)
                    {
                        int dropped = 0;
                        foreach (JsonElement entry in ids.EnumerateArray())
                        {
                            string id = entry.ValueKind == JsonValueKind.String ? entry.GetString()
                                : entry.ValueKind == JsonValueKind.Number ? entry.GetRawText()
                                : null;
                            id = id == null ? null : id.Trim();
                            if (!InviteGate.IsSteamId64(id))
                            {
                                dropped++;
                                continue;
                            }

                            if (config.InvitedSteamIds.Count >= MaxInvited)
                            {
                                dropped++;
                                continue;
                            }

                            config.InvitedSteamIds.Add(id);
                        }

                        if (dropped > 0)
                        {
                            config.Notes.Add(dropped + " entr" + (dropped == 1 ? "y" : "ies") + " on the invited list " + (dropped == 1 ? "was" : "were") + " not a Steam ID (17 digits) or went past the limit of " + MaxInvited + ", and " + (dropped == 1 ? "was" : "were") + " left out");
                        }
                    }

                    if (invited.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String)
                    {
                        string text = CleanMessage(message.GetString());
                        if (text.Length > 0)
                        {
                            config.InvitedOnlyMessage = text;
                        }
                    }
                }
            }

            return config;
        }

        /// <summary>One plain line that says what is switched on, for the log.</summary>
        public string Describe()
        {
            return "idle saver " + (IdleSaverEnabled ? "ON (" + IdleFrameRate + " frames a second when empty, after " + IdleGraceSeconds + "s)" : "off")
                + ", live feed " + (LiveFeedEnabled ? "ON (every " + LiveFeedIntervalSeconds + "s)" : "off")
                + ", invited players only " + (InvitedOnlyEnabled ? "ON (" + InvitedSteamIds.Count + " on the list)" : "off");
        }

        /// <summary>True when two settings objects would make the add-on behave the same.</summary>
        public bool SameAs(AddonConfig other)
        {
            return other != null
                && IdleSaverEnabled == other.IdleSaverEnabled
                && IdleFrameRate == other.IdleFrameRate
                && IdleGraceSeconds == other.IdleGraceSeconds
                && LiveFeedEnabled == other.LiveFeedEnabled
                && LiveFeedIntervalSeconds == other.LiveFeedIntervalSeconds
                && InvitedOnlyEnabled == other.InvitedOnlyEnabled
                && string.Equals(InvitedOnlyMessage, other.InvitedOnlyMessage, StringComparison.Ordinal)
                && InvitedSteamIds.SetEquals(other.InvitedSteamIds);
        }

        private static bool TryObject(JsonElement parent, string name, out JsonElement value)
        {
            return parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
        }

        // A switch is on only for a real true, or the word "on".
        private static bool ReadSwitch(JsonElement parent, string name, bool fallback)
        {
            if (!parent.TryGetProperty(name, out JsonElement value))
            {
                return fallback;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.String:
                    string text = (value.GetString() ?? string.Empty).Trim();
                    if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("on", StringComparison.OrdinalIgnoreCase) || text == "1")
                    {
                        return true;
                    }

                    if (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text.Equals("off", StringComparison.OrdinalIgnoreCase) || text == "0")
                    {
                        return false;
                    }

                    return fallback;
                case JsonValueKind.Number:
                    return value.TryGetInt32(out int number) ? number == 1 : fallback;
                default:
                    return fallback;
            }
        }

        private static int ReadInt(JsonElement parent, string name, int fallback, int min, int max, string label, List<string> notes)
        {
            if (!parent.TryGetProperty(name, out JsonElement value))
            {
                return fallback;
            }

            int number;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number))
            {
            }
            else if (value.ValueKind == JsonValueKind.String && int.TryParse((value.GetString() ?? string.Empty).Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out number))
            {
            }
            else
            {
                notes.Add(label + " was not a whole number; using " + fallback);
                return fallback;
            }

            if (number < min)
            {
                notes.Add(label + " " + number + " is below " + min + "; using " + min);
                return min;
            }

            if (number > max)
            {
                notes.Add(label + " " + number + " is above " + max + "; using " + max);
                return max;
            }

            return number;
        }

        // The message is shown to a player: one line, printable, not long.
        private static string CleanMessage(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            System.Text.StringBuilder clean = new System.Text.StringBuilder(text.Length);
            foreach (char c in text)
            {
                clean.Append(char.IsControl(c) ? ' ' : c);
            }

            string result = clean.ToString().Trim();
            return result.Length > 200 ? result.Substring(0, 200).Trim() : result;
        }
    }
}
