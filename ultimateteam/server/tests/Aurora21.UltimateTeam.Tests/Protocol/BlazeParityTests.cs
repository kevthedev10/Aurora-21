using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Protocol.Fire2;
using Aurora21.UltimateTeam.Protocol.Tdf;
using Aurora21.UltimateTeam.Server.Blaze;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

/// <summary>
/// Every Blaze reply and notification compared byte-for-byte with payloads captured from
/// server.py (clock frozen at 1700000000, synthetic session token). One vector deliberately differs from server.py:
/// the census subscription reply (10/5) now carries real timeouts instead of echoing RSUB.
/// </summary>
public sealed class BlazeParityTests
{
    [Fact]
    public void RepliesAndNotificationsMatchPythonBytes()
    {
        using var arranged = TestRuntime.Create(time: new TestRuntime.FixedTime(1700000000));
        arranged.Game.Store().SetAccount(arranged.Game.Store().Credits(), arranged.Game.Store().NextItemId(), arranged.Game.Store().Sid(),
            "LOCAL15TOKEN-FIXEDTOKEN", arranged.Game.Store().Established(), null, 0);
        // Account metadata is cached while a club is open. Reopen after arranging the synthetic token.
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1700000000), connectionString: arranged.Detach());
        // The Python vectors were captured when PreAuth's MAID was the club persona; each connection now draws its own.
        var responses = new BlazeResponses(rt.Fut) { MachineId = rt.Game.Config.PersonaId };
        var vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "blaze-python-vectors.json")))!;

        foreach (var vector in vectors["responses"]!.AsArray())
        {
            var request = Convert.FromHexString(vector!["req"]!.GetValue<string>());
            var frame = Fire2Frame.Parse(Fire2Frame.Build(vector["c"]!.GetValue<int>(), vector["k"]!.GetValue<int>(), 1, Fire2MessageType.Message, request));
            var reply = responses.Respond(frame);
            var label = vector["label"]!.GetValue<string>();
            Assert.Equal(label, reply.Label);
            Assert.True(vector["payload"]!.GetValue<string>() == Convert.ToHexString(reply.Payload).ToLowerInvariant(), $"payload differs for {label}");
        }

        var notifications = responses.LoginNotifications();
        var expected = vectors["notifications"]!.AsArray();
        Assert.Equal(expected.Count, notifications.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i]!["c"]!.GetValue<int>(), notifications[i].Component);
            Assert.Equal(expected[i]!["k"]!.GetValue<int>(), notifications[i].Command);
            Assert.Equal(expected[i]!["label"]!.GetValue<string>(), notifications[i].Label);
            Assert.Equal(expected[i]!["payload"]!.GetValue<string>(), Convert.ToHexString(notifications[i].Payload).ToLowerInvariant());
        }
        Assert.Equal(vectors["report"]!.GetValue<string>(), Convert.ToHexString(BlazeResponses.GameReportingResult(42).Payload).ToLowerInvariant());
    }

    /// <summary>
    /// FIFA 21 reads each struct's members in ascending tag order and leaves any member that follows a higher tag at its
    /// default, without an error. The login replies are the known exception: they keep the retail-trace layout, so the
    /// client never applies their SESS block and takes the session from the UserSessions UserAuthenticated notification.
    /// Sorting them would change what the client applies at login, so that needs its own live test first.
    /// </summary>
    [Fact]
    public void RepliesAndNotificationsWriteMembersInTagOrder()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1700000000));
        var responses = new BlazeResponses(rt.Fut);
        var vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "blaze-python-vectors.json")))!;
        foreach (var vector in vectors["responses"]!.AsArray())
        {
            var request = Convert.FromHexString(vector!["req"]!.GetValue<string>());
            var reply = responses.Respond(Fire2Frame.Parse(Fire2Frame.Build(vector["c"]!.GetValue<int>(), vector["k"]!.GetValue<int>(), 1, Fire2MessageType.Message, request)));
            if (reply.Label.StartsWith("Authentication.LocalLogin", StringComparison.Ordinal)
                || reply.Label.StartsWith("Authentication.AccountPersona", StringComparison.Ordinal))
            {
                continue;
            }
            var disorder = TdfShape.OrderViolations(reply.Payload);
            Assert.True(disorder.Count == 0, $"{reply.Label}: {string.Join("; ", disorder)}");
        }
        foreach (var notification in responses.LoginNotifications())
        {
            var disorder = TdfShape.OrderViolations(notification.Payload);
            Assert.True(disorder.Count == 0, $"{notification.Label}: {string.Join("; ", disorder)}");
        }
        Assert.Empty(TdfShape.OrderViolations(BlazeResponses.GameReportingResult(42).Payload));
    }

    /// <summary>The census subscription reply carries real timeouts; zeros made the client re-subscribe back to back.</summary>
    [Fact]
    public void CensusSubscriptionReplyCarriesTimeouts()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1700000000));
        var responses = new BlazeResponses(rt.Fut);
        var reply = responses.Respond(Fire2Frame.Parse(Fire2Frame.Build(BlazeComponent.CensusData, CensusDataCommand.SubscribeToCensusDataUpdates, 1,
            Fire2MessageType.Message, new TdfWriter().Int("RSUB", 1).ToArray())));
        Assert.True(TdfReader.TryParse(reply.Payload, out var fields));
        Assert.Equal(new[] { "CNP", "NTMT", "RTMT" }, fields.Select(field => field.Tag));
        Assert.All(fields, field => Assert.True(field.Integer >= 1_000_000, $"{field.Tag} must be at least 1 s"));
    }

    /// <summary>
    /// Opening Friendlies (2026-10-08 live requests): getStatGroup("MyFriendlies") gets a named group, and
    /// getStatsByGroupAsync an empty ack followed by the values notification echoing its view id with LAST set, without
    /// which FIFA waited on the menu.
    /// </summary>
    [Fact]
    public void FriendliesStatsRequestsAreAnswered()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1700000000));
        var responses = new BlazeResponses(rt.Fut);
        BlazeReply Ask(int command, string hex) => responses.Respond(Fire2Frame.Parse(Fire2Frame.Build(BlazeComponent.Stats, command, 1,
            Fire2MessageType.Message, Convert.FromHexString(hex))));

        var group = Ask(StatsCommand.GetStatGroup, "ba1b65010d4d79467269656e646c69657300");
        Assert.NotEqual(BlazeFollowUp.Unknown, group.FollowUp);
        Assert.Equal("MyFriendlies", TdfReader.FindString(group.Payload, "NAME"));
        Assert.Empty(TdfShape.OrderViolations(group.Payload));

        var values = Ask(StatsCommand.GetStatsByGroupAsync,
            "969900040001b7dfe1ab0dba1b65010100c23d320001c2f9a60000c32a640000c34e700000d29b650000da99000003");
        Assert.NotEqual(BlazeFollowUp.Unknown, values.FollowUp);
        Assert.Empty(values.Payload);
        var notification = Assert.IsType<BlazeNotification>(values.Notification);
        Assert.Equal((BlazeComponent.Stats, StatsNotification.GetStatsAsync), (notification.Component, notification.Command));
        Assert.Equal(3L, TdfReader.FindInteger(notification.Payload, "VID"));
        Assert.Equal(1L, TdfReader.FindInteger(notification.Payload, "LAST"));
        Assert.Empty(TdfShape.OrderViolations(notification.Payload));
        Assert.True(TdfReader.TryParse(notification.Payload, out var fields));
        var rows = TdfReader.Find(fields, "STS", TdfType.Struct)!.Children.Single(child => child.Tag == "STAT").Children;
        Assert.Single(rows);
    }
}
