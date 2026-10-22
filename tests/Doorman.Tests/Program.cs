using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Doorman.Core;

// The add-on's logic, checked without a game. Nobody may join the test game server, so the
// invited-list decision, the idle rule, the settings reader and the failure lines are proven here.
// Run: dotnet run --project tests/Doorman.Tests   (exits 1 on any failure)
internal static class Program
{
    private static int _checks;
    private static readonly List<string> Failures = new List<string>();

    private static void Check(string label, bool ok, string detail = "")
    {
        _checks++;
        if (!ok)
        {
            Failures.Add(label + (detail.Length > 0 ? " -- " + detail : ""));
        }
    }

    private const string Invited = "76561198000000001";
    private const string Other = "76561198000000002";

    private static int Main()
    {
        Settings();
        Invites();
        Idle();
        Feed();
        Steps();

        if (Failures.Count > 0)
        {
            Console.WriteLine("FAIL (" + Failures.Count + " of " + _checks + ")");
            foreach (string failure in Failures)
            {
                Console.WriteLine(" - " + failure);
            }

            return 1;
        }

        Console.WriteLine("OK: " + _checks + " checks");
        return 0;
    }

    // ---- settings file

    private static void Settings()
    {
        AddonConfig d = AddonConfig.Defaults();
        Check("defaults: idle saver off", !d.IdleSaverEnabled);
        Check("defaults: invited only off", !d.InvitedOnlyEnabled && d.InvitedSteamIds.Count == 0);
        Check("defaults: live feed on, every 5s", d.LiveFeedEnabled && d.LiveFeedIntervalSeconds == 5);
        Check("defaults: idle 10 frames, 15s quiet", d.IdleFrameRate == 10 && d.IdleGraceSeconds == 15);

        AddonConfig empty = AddonConfig.Parse("{}");
        Check("empty object: readable, same as defaults", empty.Readable && empty.SameAs(d) && empty.Notes.Count == 0);

        // a full settings file, as a hosting tool would write it (keep this text identical in any tool that writes the
        // file, so the two sides cannot drift apart)
        string full = "{\"version\":1,\"idleSaver\":{\"enabled\":true,\"frameRate\":10,\"graceSeconds\":15},\"liveFeed\":{\"enabled\":true,\"intervalSeconds\":5},\"invitedOnly\":{\"enabled\":true,\"steamIds\":[\"" + Invited + "\",\"" + Other + "\"]}}";
        AddonConfig p = AddonConfig.Parse(full);
        Check("full file: all three read", p.Readable && p.IdleSaverEnabled && p.LiveFeedEnabled && p.InvitedOnlyEnabled);
        Check("full file: the list", p.InvitedSteamIds.Count == 2 && p.InvitedSteamIds.Contains(Invited) && p.InvitedSteamIds.Contains(Other));
        Check("full file: no message given keeps the plain default", p.InvitedOnlyMessage == AddonConfig.DefaultInvitedOnlyMessage);
        Check("a message in the file is used", AddonConfig.Parse(full.Replace("]}}", "],\"message\":\"Private server.\"}}")).InvitedOnlyMessage == "Private server.");
        // a written file with every switch at its default
        AddonConfig plain = AddonConfig.Parse("{\"version\":1,\"idleSaver\":{\"enabled\":false,\"frameRate\":10,\"graceSeconds\":15},\"liveFeed\":{\"enabled\":true,\"intervalSeconds\":5},\"invitedOnly\":{\"enabled\":false,\"steamIds\":[]}}");
        Check("file with the defaults: the same as no file at all", plain.Readable && plain.SameAs(d) && plain.Notes.Count == 0);
        Check("full file: no notes", p.Notes.Count == 0, string.Join("; ", p.Notes));
        Check("describe: says what is on", p.Describe() == "idle saver ON (10 frames a second when empty, after 15s), live feed ON (every 5s), invited players only ON (2 on the list)", p.Describe());
        Check("describe: says what is off", d.Describe() == "idle saver off, live feed ON (every 5s), invited players only off", d.Describe());

        // a switch is on only when the file really says so
        Check("switch: the word on", AddonConfig.Parse("{\"idleSaver\":{\"enabled\":\"on\"}}").IdleSaverEnabled);
        Check("switch: the word true", AddonConfig.Parse("{\"idleSaver\":{\"enabled\":\"true\"}}").IdleSaverEnabled);
        Check("switch: 1", AddonConfig.Parse("{\"idleSaver\":{\"enabled\":1}}").IdleSaverEnabled);
        Check("switch: any other word leaves it off", !AddonConfig.Parse("{\"idleSaver\":{\"enabled\":\"yes please\"}}").IdleSaverEnabled);
        Check("switch: null leaves it off", !AddonConfig.Parse("{\"idleSaver\":{\"enabled\":null}}").IdleSaverEnabled);
        Check("switch: missing section leaves it off", !AddonConfig.Parse("{\"invitedOnly\":{\"steamIds\":[\"" + Invited + "\"]}}").InvitedOnlyEnabled);
        Check("switch: the feed can be turned off", !AddonConfig.Parse("{\"liveFeed\":{\"enabled\":false}}").LiveFeedEnabled);
        Check("switch: a section that is not an object is ignored", !AddonConfig.Parse("{\"idleSaver\":true}").IdleSaverEnabled);

        // numbers are kept inside safe limits
        AddonConfig low = AddonConfig.Parse("{\"idleSaver\":{\"enabled\":true,\"frameRate\":0,\"graceSeconds\":-5},\"liveFeed\":{\"intervalSeconds\":0}}");
        Check("limits: frame rate never below 2", low.IdleFrameRate == 2 && low.Notes.Count == 3, string.Join("; ", low.Notes));
        Check("limits: quiet stretch never negative", low.IdleGraceSeconds == 0);
        Check("limits: feed never faster than 2s", low.LiveFeedIntervalSeconds == 2);
        AddonConfig high = AddonConfig.Parse("{\"idleSaver\":{\"frameRate\":500,\"graceSeconds\":100000},\"liveFeed\":{\"intervalSeconds\":3600}}");
        Check("limits: frame rate never above 60", high.IdleFrameRate == 60);
        Check("limits: quiet stretch at most 10 minutes", high.IdleGraceSeconds == 600);
        Check("limits: feed at least once a minute", high.LiveFeedIntervalSeconds == 60);
        AddonConfig words = AddonConfig.Parse("{\"idleSaver\":{\"frameRate\":\"fast\"},\"liveFeed\":{\"intervalSeconds\":\"7\"}}");
        Check("limits: a word where a number belongs gives the default and a note", words.IdleFrameRate == 10 && words.Notes.Count == 1);
        Check("limits: a number written as text is read", words.LiveFeedIntervalSeconds == 7);

        // the list takes Steam IDs only
        AddonConfig list = AddonConfig.Parse("{\"invitedOnly\":{\"enabled\":true,\"steamIds\":[\"" + Invited + "\",\" " + Other + " \",\"" + Invited + "\",\"12345\",\"7656119800000000x\",\"86561198000000001\",null,42,76561198000000003]}}");
        Check("list: spaces trimmed, repeats dropped, a bare number read", list.InvitedSteamIds.Count == 3 && list.InvitedSteamIds.Contains(Other) && list.InvitedSteamIds.Contains("76561198000000003"), list.InvitedSteamIds.Count.ToString());
        Check("list: what was left out is said once", list.Notes.Count == 1 && list.Notes[0].StartsWith("5 entries on the invited list were not a Steam ID"), string.Join("; ", list.Notes));
        Check("list: on with nobody listed is still on (nobody gets in)", AddonConfig.Parse("{\"invitedOnly\":{\"enabled\":true}}").InvitedOnlyEnabled);
        Check("list: a list that is not a list is ignored", AddonConfig.Parse("{\"invitedOnly\":{\"enabled\":true,\"steamIds\":\"" + Invited + "\"}}").InvitedSteamIds.Count == 0);

        // the message is shown to a person
        AddonConfig msg = AddonConfig.Parse("{\"invitedOnly\":{\"message\":\"  line one\\nline two\\t!  \"}}");
        Check("message: one clean line", msg.InvitedOnlyMessage == "line one line two !", msg.InvitedOnlyMessage);
        Check("message: empty keeps the default", AddonConfig.Parse("{\"invitedOnly\":{\"message\":\"   \"}}").InvitedOnlyMessage == AddonConfig.DefaultInvitedOnlyMessage);
        Check("message: never longer than 200", AddonConfig.Parse("{\"invitedOnly\":{\"message\":\"" + new string('a', 500) + "\"}}").InvitedOnlyMessage.Length == 200);
        Check("message: the default is plain and names no hardware or supplier", AddonConfig.DefaultInvitedOnlyMessage == "This server is for invited players only. Ask the owner to add your Steam ID to the invited list.");

        // a file that is not a settings file is refused, so good settings in use are kept
        foreach (string bad in new[] { "", "   ", "not json", "{\"idleSaver\":{\"enabled\":true}", "[1,2]", "\"text\"", "null" })
        {
            AddonConfig b = AddonConfig.Parse(bad);
            Check("unreadable: " + (bad.Length == 0 ? "(empty)" : bad), !b.Readable && !b.IdleSaverEnabled && !b.InvitedOnlyEnabled && b.Notes.Count == 1);
        }

        Check("unreadable: null", !AddonConfig.Parse(null).Readable);
        Check("comments and a trailing comma are tolerated", AddonConfig.Parse("{ /* a note */ \"idleSaver\": {\"enabled\": true,}, }").IdleSaverEnabled);

        // same / different, for "settings changed" in the log
        Check("same: two reads of one file", p.SameAs(AddonConfig.Parse(full)));
        Check("different: one more invited", !p.SameAs(AddonConfig.Parse(full.Replace("\"" + Other + "\"", "\"" + Other + "\",\"76561198000000009\""))));
        Check("different: the switch", !p.SameAs(AddonConfig.Parse(full.Replace("\"idleSaver\":{\"enabled\":true", "\"idleSaver\":{\"enabled\":false"))));
        Check("different: nothing to compare with", !p.SameAs(null));
    }

    // ---- invited players only

    private static AddonConfig InvitedOnly(bool on)
    {
        return AddonConfig.Parse("{\"invitedOnly\":{\"enabled\":" + (on ? "true" : "false") + ",\"steamIds\":[\"" + Invited + "\"]}}");
    }

    private static void Invites()
    {
        Check("steam id: a real one", InviteGate.IsSteamId64(Invited));
        Check("steam id: 16 digits", !InviteGate.IsSteamId64("7656119800000000"));
        Check("steam id: 18 digits", !InviteGate.IsSteamId64("765611980000000011"));
        Check("steam id: a letter", !InviteGate.IsSteamId64("7656119800000000a"));
        Check("steam id: wrong start", !InviteGate.IsSteamId64("12345678901234567"));
        Check("steam id: the mod's stand-in for its own hidden player", !InviteGate.IsSteamId64("ghost_host") && !InviteGate.IsSteamId64("0"));
        Check("steam id: empty and null", !InviteGate.IsSteamId64("") && !InviteGate.IsSteamId64(null));
        Check("steam id: other digit shapes", !InviteGate.IsSteamId64("７６５６１１９８０００００００１"));

        AddonConfig on = InvitedOnly(true);
        AddonConfig off = InvitedOnly(false);

        ConnectionIdentity confirmedInvited = new ConnectionIdentity { ConfirmedSteamId = Invited, ReportedSteamId = Invited, ServerConfirmsIdentity = true };
        ConnectionIdentity confirmedOther = new ConnectionIdentity { ConfirmedSteamId = Other, ReportedSteamId = Other, ServerConfirmsIdentity = true };

        // switched off: everybody, always
        Check("off: an invited player", InviteGate.Decide(off, confirmedInvited) == InviteVerdict.Allow);
        Check("off: anybody else", InviteGate.Decide(off, confirmedOther) == InviteVerdict.Allow);
        Check("off: somebody Steam has not confirmed", InviteGate.Decide(off, new ConnectionIdentity { ServerConfirmsIdentity = true }) == InviteVerdict.Allow);
        Check("off: no settings at all", InviteGate.Decide(null, confirmedOther) == InviteVerdict.Allow);
        Check("off: the defaults", InviteGate.Decide(AddonConfig.Defaults(), confirmedOther) == InviteVerdict.Allow);

        // switched on, the mod's Steam authentication on (Steam confirms every player)
        Check("on: Steam confirmed an invited player", InviteGate.Decide(on, confirmedInvited) == InviteVerdict.Allow);
        Check("on: Steam confirmed somebody not on the list", InviteGate.Decide(on, confirmedOther) == InviteVerdict.TurnAway);
        Check("on: still in the Steam check", InviteGate.Decide(on, new ConnectionIdentity { ServerConfirmsIdentity = true }) == InviteVerdict.Wait);
        Check("on: the game CLAIMS an invited ID but Steam has not confirmed it", InviteGate.Decide(on, new ConnectionIdentity { ReportedSteamId = Invited, ServerConfirmsIdentity = true }) == InviteVerdict.Wait);
        Check("on: claims an invited ID, Steam confirms a different one", InviteGate.Decide(on, new ConnectionIdentity { ReportedSteamId = Invited, ConfirmedSteamId = Other, ServerConfirmsIdentity = true }) == InviteVerdict.TurnAway);
        Check("on: claims another ID, Steam confirms an invited one", InviteGate.Decide(on, new ConnectionIdentity { ReportedSteamId = Other, ConfirmedSteamId = Invited, ServerConfirmsIdentity = true }) == InviteVerdict.Allow);
        Check("on: all the way in with nothing confirmed is not left in", InviteGate.Decide(on, new ConnectionIdentity { ReportedSteamId = Invited, ServerConfirmsIdentity = true, JoinFinished = true }) == InviteVerdict.TurnAway);
        Check("on: a confirmed ID that is not a Steam ID counts as not confirmed", InviteGate.Decide(on, new ConnectionIdentity { ConfirmedSteamId = "ghost_host", ServerConfirmsIdentity = true }) == InviteVerdict.Wait);
        Check("on: the server's own hidden player is never turned away", InviteGate.Decide(on, new ConnectionIdentity { IsLoopback = true, ServerConfirmsIdentity = true, JoinFinished = true }) == InviteVerdict.Allow);
        Check("on: an empty list turns everybody away", InviteGate.Decide(AddonConfig.Parse("{\"invitedOnly\":{\"enabled\":true}}"), confirmedInvited) == InviteVerdict.TurnAway);

        // switched on, a server that does not ask Steam: the reported ID is all there is
        Check("no Steam check: reported and invited", InviteGate.Decide(on, new ConnectionIdentity { ReportedSteamId = Invited }) == InviteVerdict.Allow);
        Check("no Steam check: reported and not invited", InviteGate.Decide(on, new ConnectionIdentity { ReportedSteamId = Other }) == InviteVerdict.TurnAway);
        Check("no Steam check: nothing reported yet", InviteGate.Decide(on, new ConnectionIdentity()) == InviteVerdict.Wait);
        Check("no Steam check: in with nothing reported", InviteGate.Decide(on, new ConnectionIdentity { JoinFinished = true }) == InviteVerdict.TurnAway);
    }

    // ---- idle saver

    private static void Idle()
    {
        double t = 0d;
        IdlePolicy idle = new IdlePolicy(() => t);

        Check("idle: off by default", !idle.IsIdle && idle.WaitMilliseconds(1d) == 0);
        idle.ServerReady();
        t = 1000d;
        Check("idle: ready but switched off never waits", !idle.IsIdle && idle.WaitMilliseconds(1d) == 0);

        // switched on: only after the server is ready and a quiet stretch
        t = 0d;
        idle = new IdlePolicy(() => t);
        idle.Configure(true, 10, 15);
        t = 100d;
        Check("idle: not while the server is still starting", !idle.IsIdle);
        idle.ServerReady();
        Check("idle: not the moment it is ready", !idle.IsIdle);
        t = 114.9d;
        Check("idle: not before the quiet stretch is over", !idle.IsIdle);
        t = 115d;
        Check("idle: after 15 quiet seconds", idle.IsIdle);
        Check("idle: a 2ms frame waits out the rest of 100ms", idle.WaitMilliseconds(2d) == 98);
        Check("idle: a frame that already took 100ms does not wait", idle.WaitMilliseconds(100d) == 0);
        Check("idle: a frame that took longer does not wait", idle.WaitMilliseconds(250d) == 0);
        Check("idle: part milliseconds round up", idle.WaitMilliseconds(99.5d) == 1);

        // a connection starting is full speed at once, before anybody has counted it
        idle.ConnectionStarted();
        Check("join: full speed the instant a connection starts", !idle.IsIdle && idle.WaitMilliseconds(2d) == 0);
        t = 200d;
        Check("join: stays at full speed however long the join takes", !idle.IsIdle);
        idle.ReportConnections(1);
        t = 5000d;
        Check("join: full speed for as long as somebody is connected", !idle.IsIdle && idle.Connections == 1);
        idle.ReportConnections(2);
        idle.ReportConnections(1);
        t = 5100d;
        Check("join: one of two leaving changes nothing", !idle.IsIdle);

        // the last one leaves
        idle.ReportConnections(0);
        Check("leave: not slow the moment the last player goes", !idle.IsIdle);
        t = 5114d;
        idle.ReportConnections(0);
        Check("leave: still full speed inside the quiet stretch", !idle.IsIdle);
        t = 5115.1d;
        idle.ReportConnections(0);
        Check("leave: slow again 15 seconds after the last player left", idle.IsIdle);

        // a connection that starts and is counted as nobody (refused: server full) still gets its stretch
        idle.ConnectionStarted();
        idle.ReportConnections(0);
        Check("refused: full speed after a connection that came to nothing", !idle.IsIdle);
        t = 5131d;
        idle.ReportConnections(0);
        Check("refused: slow again after the quiet stretch", idle.IsIdle);

        // a save: full speed from the moment it starts until a short tail after it is reported finished
        idle.SaveStarted();
        Check("save: full speed the moment a save starts", !idle.IsIdle && idle.WaitMilliseconds(2d) == 0);
        t = 5136d;
        Check("save: still full speed while it runs", !idle.IsIdle);
        idle.SaveFinished();
        t = 5145.9d;
        Check("save: a short full-speed tail after it is reported finished", !idle.IsIdle);
        t = 5146.1d;
        Check("save: slow again 10 seconds after the save finished, not a minute", idle.IsIdle);
        idle.SaveStarted();
        t = 5200d;
        Check("save: a save nobody reports finished holds full speed for a minute", !idle.IsIdle);
        t = 5206.2d;
        Check("save: and no longer", idle.IsIdle);
        idle.SaveStarted();
        idle.ConnectionStarted();
        idle.SaveFinished();
        idle.ReportConnections(1);
        t = 5300d;
        Check("save: finishing a save never makes a server with a player on it slow", !idle.IsIdle);
        idle.ReportConnections(0);
        t = 5316d;
        idle.ReportConnections(0);
        Check("save: slow again once that player has gone", idle.IsIdle);

        // switching
        idle.Configure(false, 10, 15);
        Check("switch off while idle: full speed at once", !idle.IsIdle && idle.WaitMilliseconds(1d) == 0);
        idle.Configure(true, 10, 15);
        Check("switch on while running: a quiet stretch first", !idle.IsIdle);
        t = 5332d;
        Check("switch on while running: then slow", idle.IsIdle);
        idle.Configure(true, 2, 15);
        Check("a new idle rate while idle applies without a gap", idle.IsIdle && idle.WaitMilliseconds(0d) == 500);
        idle.Configure(true, 0, 15);
        Check("the rule keeps the rate inside 2 to 60 by itself", idle.IdleFrameRate == 2);
        idle.Configure(true, 1000, 15);
        Check("the rule keeps the rate inside 2 to 60 by itself (high)", idle.IdleFrameRate == 60 && idle.WaitMilliseconds(0d) == 17);

        // stopping
        idle.ServerStopping();
        Check("stopping: never slow while the server shuts down", !idle.IsIdle);

        // odd input
        idle = new IdlePolicy(() => t);
        idle.Configure(true, 10, 0);
        idle.ServerReady();
        Check("no quiet stretch asked for: idle at once when ready and empty", idle.IsIdle);
        idle.ReportConnections(-3);
        Check("a negative count is nobody", idle.Connections == 0 && idle.IsIdle);
        bool threw = false;
        try { new IdlePolicy(null); } catch (ArgumentNullException) { threw = true; }
        Check("a missing clock is refused", threw);
    }

    // ---- live feed

    private static void Feed()
    {
        DateTime written = new DateTime(2026, 10, 9, 19, 30, 5, DateTimeKind.Utc);
        FeedSnapshot empty = new FeedSnapshot
        {
            AddonVersion = "1.0.0",
            WrittenUtc = written,
            ReadyUtc = written.AddMinutes(-10),
            ClockAvailable = true,
            Day = 12,
            Hour = 7,
            Minute = 5,
            MaxPlayers = 16,
            SaveFilesUtc = written.AddMinutes(-3),
            IdleSaverOn = true,
            IdleNow = true,
            IdleFrameRate = 10,
            NormalFrameRate = 60,
            MeasuredFrameRate = 9.96d
        };

        string json = LiveFeed.ToJson(empty);
        string expected = "{\"format\":1,\"addon\":\"1.0.0\",\"written\":\"2026-10-09T19:30:05Z\",\"writtenUnix\":1791574205,\"ready\":\"2026-10-09T19:20:05Z\","
            + "\"clock\":{\"available\":true,\"day\":12,\"hour\":7,\"minute\":5,\"time\":\"07:05\"},"
            + "\"lastSave\":{\"files\":\"2026-10-09T19:27:05Z\",\"reported\":null},"
            + "\"playerCount\":0,\"maxPlayers\":16,\"players\":[],"
            + "\"frameRate\":{\"idleSaver\":true,\"idleNow\":true,\"idleLimit\":10,\"normalLimit\":60,\"measured\":10},"
            + "\"invitedOnly\":{\"on\":false,\"checking\":false,\"listed\":0}}";
        Check("feed: nobody connected, exact text", json == expected, json);

        FeedSnapshot two = new FeedSnapshot { AddonVersion = "1.0.0", WrittenUtc = written, MaxPlayers = 16, NormalFrameRate = 60, IdleFrameRate = 10, InvitedOnlyOn = true, InvitedCount = 3 };
        two.Players.Add(new FeedPlayer { Name = "Mick \"the\" <b>Dealer</b> \\ é\n", SteamId = Invited, ConnectedUtc = written.AddSeconds(-125), Joined = true, HasPosition = true, X = -12.34f, Z = 45.678f });
        two.Players.Add(new FeedPlayer { Name = null, SteamId = null, ConnectedUtc = written.AddSeconds(5), Joined = false, HasPosition = false });
        two.Players.Add(new FeedPlayer { Name = "NaN", SteamId = Other, ConnectedUtc = written, Joined = true, HasPosition = true, X = float.NaN, Z = 1f });
        string text = LiveFeed.ToJson(two);
        using (JsonDocument doc = JsonDocument.Parse(text))
        {
            JsonElement root = doc.RootElement;
            JsonElement players = root.GetProperty("players");
            Check("feed: it is valid JSON whatever a player is called", players.GetArrayLength() == 3);
            Check("feed: the name comes back exactly", players[0].GetProperty("name").GetString() == "Mick \"the\" <b>Dealer</b> \\ é\n");
            Check("feed: Steam ID as text (a 17-digit number does not survive JavaScript)", players[0].GetProperty("steamId").ValueKind == JsonValueKind.String && players[0].GetProperty("steamId").GetString() == Invited);
            Check("feed: time connected", players[0].GetProperty("connected").GetString() == "2026-10-09T19:28:00Z" && players[0].GetProperty("seconds").GetInt64() == 125);
            Check("feed: position to one decimal", players[0].GetProperty("x").GetDouble() == -12.3d && players[0].GetProperty("z").GetDouble() == 45.7d, players[0].GetRawText());
            Check("feed: a player still being let in has no position and is marked", players[1].GetProperty("x").ValueKind == JsonValueKind.Null && players[1].GetProperty("z").ValueKind == JsonValueKind.Null && !players[1].GetProperty("joined").GetBoolean());
            Check("feed: missing name and ID are empty text, never null", players[1].GetProperty("name").GetString() == "" && players[1].GetProperty("steamId").GetString() == "");
            Check("feed: time connected is never negative", players[1].GetProperty("seconds").GetInt64() == 0);
            Check("feed: a position that is not a number is left out", players[2].GetProperty("x").ValueKind == JsonValueKind.Null && players[2].GetProperty("z").ValueKind == JsonValueKind.Null);
            Check("feed: the count matches the list", root.GetProperty("playerCount").GetInt32() == 3);
            Check("feed: clock not available says so and gives no numbers", !root.GetProperty("clock").GetProperty("available").GetBoolean() && !root.GetProperty("clock").TryGetProperty("day", out _));
            Check("feed: no save yet is null twice", root.GetProperty("lastSave").GetProperty("files").ValueKind == JsonValueKind.Null && root.GetProperty("lastSave").GetProperty("reported").ValueKind == JsonValueKind.Null);
            Check("feed: not measured yet is null", root.GetProperty("frameRate").GetProperty("measured").ValueKind == JsonValueKind.Null);
            Check("feed: ready not known is null", root.GetProperty("ready").ValueKind == JsonValueKind.Null);
            Check("feed: invited only shows the switch and the size of the list, never the list", root.GetProperty("invitedOnly").GetProperty("on").GetBoolean() && root.GetProperty("invitedOnly").GetProperty("listed").GetInt32() == 3 && !text.Contains("steamIds"));
            Check("feed: on, but the check has not run, is not checking", !root.GetProperty("invitedOnly").GetProperty("checking").GetBoolean());
        }

        two.InvitedChecking = true;
        using (JsonDocument doc = JsonDocument.Parse(LiveFeed.ToJson(two)))
        {
            JsonElement invited = doc.RootElement.GetProperty("invitedOnly");
            Check("feed: on and the check running is checking", invited.GetProperty("on").GetBoolean() && invited.GetProperty("checking").GetBoolean());
        }

        two.InvitedOnlyOn = false;
        using (JsonDocument doc = JsonDocument.Parse(LiveFeed.ToJson(two)))
        {
            Check("feed: switched off is never checking, whatever the step last did", !doc.RootElement.GetProperty("invitedOnly").GetProperty("checking").GetBoolean());
        }

        Check("feed: one line, no growth with time", !json.Contains("\n") && LiveFeed.ToJson(empty).Length == json.Length);

        // the file itself: replaced whole, nothing left beside it
        string folder = Path.Combine(Path.GetTempPath(), "ssaddon-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "Doorman.live.json");
            Check("file: first write", LiveFeed.WriteAtomic(path, json) && File.ReadAllText(path) == json);
            Check("file: no byte-order mark (PHP's json_decode refuses one)", File.ReadAllBytes(path)[0] == (byte)'{');
            Check("file: second write replaces the first", LiveFeed.WriteAtomic(path, text) && File.ReadAllText(path) == text);
            Check("file: no temp file left behind", Directory.GetFiles(folder).Length == 1);
            for (int i = 0; i < 200; i++)
            {
                LiveFeed.WriteAtomic(path, i % 2 == 0 ? json : text);
            }

            Check("file: 200 writes later there is still one file", Directory.GetFiles(folder).Length == 1 && new FileInfo(path).Length == System.Text.Encoding.UTF8.GetByteCount(text));
            Check("file: a folder that is not there is a quiet no", !LiveFeed.WriteAtomic(Path.Combine(folder, "missing", "x.json"), json));

            // last save time from the save folder's own files
            string save = Path.Combine(folder, "DedicatedServerSave");
            Check("save time: no folder yet", LiveFeed.NewestSaveFileUtc(save) == null && LiveFeed.NewestSaveFileUtc(null) == null && LiveFeed.NewestSaveFileUtc("") == null);
            Directory.CreateDirectory(save);
            Check("save time: an empty folder", LiveFeed.NewestSaveFileUtc(save) == null);
            File.WriteAllText(Path.Combine(save, "Game.json"), "{}");
            File.WriteAllText(Path.Combine(save, "Time.json"), "{}");
            File.WriteAllText(Path.Combine(save, "notes.txt"), "not a save file");
            DateTime older = new DateTime(2026, 10, 9, 18, 0, 0, DateTimeKind.Utc);
            DateTime newer = new DateTime(2026, 10, 9, 18, 30, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(Path.Combine(save, "Game.json"), older);
            File.SetLastWriteTimeUtc(Path.Combine(save, "Time.json"), newer);
            File.SetLastWriteTimeUtc(Path.Combine(save, "notes.txt"), newer.AddHours(5));
            Check("save time: the newest of the save's own files", LiveFeed.NewestSaveFileUtc(save) == newer, LiveFeed.NewestSaveFileUtc(save).ToString());
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    // ---- failure lines (each step on its own; the log says what fails without filling up)

    private static void Steps()
    {
        double t = 0d;
        StepHealth step = new StepHealth("invite check", () => t);

        Check("step: a step that never failed says nothing when it works", step.Succeeded() == null && !step.Failing);
        Check("step: worked just now", step.WorkedWithin(5d));
        t = 6d;
        Check("step: worked, but not within the window", !step.WorkedWithin(5d));
        step.Succeeded();

        string first = step.Failed(new InvalidOperationException("Clients is gone"));
        Check("step: the first failure is said at once, with the exception", first == "Invite check failed, and is tried again each time: InvalidOperationException: Clients is gone.", first);
        Check("step: failing, so not working within any window", step.Failing && !step.WorkedWithin(1000d) && step.FailuresInARow == 1);

        // the same failure four times a second for almost five minutes: no more lines
        int lines = 0;
        for (int i = 1; i < 1199; i++)
        {
            t = 6d + i * 0.25d;
            if (step.Failed(new InvalidOperationException("Clients is gone")) != null)
            {
                lines++;
            }
        }

        Check("step: the same failure again is only counted", lines == 0 && step.FailuresInARow == 1199, lines.ToString());
        t = 306d;
        string again = step.Failed(new InvalidOperationException("Clients is gone"));
        Check("step: five minutes on it is said again, with a count", again == "Invite check is still failing (1198 failures since the last line): InvalidOperationException: Clients is gone.", again);
        t = 307d;
        Check("step: and then quiet again", step.Failed(new InvalidOperationException("Clients is gone")) == null);

        // a different failure: said, but never two lines within 10 seconds
        t = 310d;
        Check("step: a new kind inside 10 seconds of the last line waits", step.Failed(new NullReferenceException("Object reference not set")) == null);
        t = 316.5d;
        string newer = step.Failed(new NullReferenceException("Object reference not set"));
        Check("step: a new kind is said once the 10 seconds are up", newer == "Invite check failed in a new way (2 failures since the last line): NullReferenceException: Object reference not set.", newer);
        t = 330d;
        Check("step: flipping back to a kind already said is only counted", step.Failed(new InvalidOperationException("Clients is gone")) == null && step.Failed(new NullReferenceException("Object reference not set")) == null);

        // works again
        t = 331d;
        string back = step.Succeeded();
        Check("step: one line when it works again", back == "Invite check is working again after 1205 failures.", back);
        Check("step: and it is working", !step.Failing && step.FailuresInARow == 0 && step.WorkedWithin(5d));
        Check("step: no second working line", step.Succeeded() == null);
        t = 332d;
        string relapse = step.Failed(new InvalidOperationException("Clients is gone"));
        Check("step: after working, the old failure is a first failure again", relapse != null && relapse.StartsWith("Invite check failed, and is tried again each time: "), relapse);
        step.Succeeded();

        // a message that changes every time (an index, a name) cannot fill the log
        StepHealth noisy = new StepHealth("player count", () => t);
        int noisyLines = 0;
        for (int i = 0; i < 5000; i++)
        {
            t = 1000d + i;
            if (noisy.Failed(new ArgumentOutOfRangeException("index", "Index " + i + " was out of range")) != null)
            {
                noisyLines++;
            }
        }

        Check("step: changing messages give at most 16 kinds, then a line every five minutes", noisyLines <= StepHealth.MaxKinds + (int)(5000 / StepHealth.RepeatSeconds) + 1, noisyLines.ToString());
        Check("step: and still at least the first", noisyLines >= 1);

        // what a line carries
        StepHealth plain = new StepHealth("writing the live feed file", () => t);
        string reason = plain.Failed(null, "Doorman.live.json could not be written five times in a row");
        Check("step: a plain reason without an exception type", reason == "Writing the live feed file failed, and is tried again each time: Doorman.live.json could not be written five times in a row.", reason);
        plain.Succeeded();
        string odd = plain.Failed("IOException", "line one\nline two\t\r  end");
        Check("step: one line, whatever the message holds", odd == "Writing the live feed file failed, and is tried again each time: IOException: line one line two end.", odd);
        plain.Succeeded();
        string longOne = plain.Failed("IOException", new string('x', 1000));
        Check("step: a long message is cut", longOne.Length < StepHealth.MaxDetail + 100, longOne.Length.ToString());
        plain.Succeeded();
        Check("step: nothing at all to say still says something", plain.Failed((Exception)null) == "Writing the live feed file failed, and is tried again each time: no reason given.");
        bool threw = false;
        try { new StepHealth("x", null); } catch (ArgumentNullException) { threw = true; }
        Check("step: a missing clock is refused", threw);
    }
}
