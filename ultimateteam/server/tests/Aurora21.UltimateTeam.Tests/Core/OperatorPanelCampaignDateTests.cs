using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed partial class OperatorPanelTests
{
    [Fact]
    public async Task CampaignDatesPreserveEventsSevenPmSummerAndWinterInstantsAndDropsHaveOwnDates()
    {
        using var setup = await CreateAsync();
        foreach (var dates in new[]
        {
            (Start: "2030-06-01", End: "2030-06-10", DropStart: "2030-06-02", DropEnd: "2030-06-04", Hour: 18),
            (Start: "2030-01-01", End: "2030-01-10", DropStart: "2030-01-02", DropEnd: "2030-01-04", Hour: 19)
        })
        {
            var campaignReply = await Send(setup.Panel, "POST", "/panel/api/content/campaigns", CampaignDatesBody(dates.Start, dates.End).ToJsonString());
            Assert.True(campaignReply.Status == 200, campaignReply.Body);
            var campaign = campaignReply.Json!;
            Assert.Equal(CampaignDateUtc(dates.Start, dates.Hour), CampaignTimestamp(campaign, "startsAt"));
            Assert.Equal(CampaignDateUtc(dates.End, dates.Hour), CampaignTimestamp(campaign, "endsAt"));
            var dropReply = await Send(setup.Panel, "POST", "/panel/api/content/drops", CampaignDropDatesBody(campaign, dates.DropStart, dates.DropEnd).ToJsonString());
            Assert.True(dropReply.Status == 200, dropReply.Body);
            Assert.Equal(campaign["id"]!.GetValue<long>(), dropReply.Json!["campaignId"]!.GetValue<long>());
            Assert.Equal(CampaignDateUtc(dates.DropStart, dates.Hour), CampaignTimestamp(dropReply.Json, "startsAt"));
            Assert.Equal(CampaignDateUtc(dates.DropEnd, dates.Hour), CampaignTimestamp(dropReply.Json, "endsAt"));
            Assert.False(dropReply.Json["inheritEnd"]!.GetValue<bool>());
            var publish = await Send(setup.Panel, "POST", $"/panel/api/content/drops/{dropReply.Json["id"]!.GetValue<long>()}/publish",
                new JsonObject { ["version"] = dropReply.Json["version"]!.DeepClone() }.ToJsonString());
            Assert.Equal(400, publish.Status);
        }
        var overview = await Send(setup.Panel, "GET", "/panel/api/content/schedule");
        Assert.Equal(200, overview.Status);
        Assert.Equal(2, overview.Json!["campaigns"]!.AsArray().Count);
        Assert.Equal(2, overview.Json["drops"]!.AsArray().Count);
        Assert.All(overview.Json["campaigns"]!.AsArray(), c => Assert.NotNull(c!["startsAt"]));
    }

    [Fact]
    public async Task CampaignDateApiRejectsMalformedMissingAndUnboundedDropDatesWithoutSaving()
    {
        using var setup = await CreateAsync();
        var response = await Send(setup.Panel, "POST", "/panel/api/content/campaigns", CampaignDatesBody("2030-06-01", "2030-06-10").ToJsonString());
        Assert.True(response.Status == 200, response.Body);
        var campaign = response.Json!;
        foreach (var dates in new[]
        {
            ("2030-05-31", "2030-06-04"), ("2030-06-02", "2030-06-11"),
            ("2030-06-03", "2030-06-03"), ("2030-06-05", "2030-06-04")
        })
        {
            var rejected = await Send(setup.Panel, "POST", "/panel/api/content/drops", CampaignDropDatesBody(campaign, dates.Item1, dates.Item2).ToJsonString());
            Assert.True(rejected.Status == 400, rejected.Body);
        }
        foreach (var missing in new[] { "startsAt", "endsAt" })
        {
            var body = CampaignDropDatesBody(campaign, "2030-06-02", "2030-06-04");
            body.Remove(missing);
            Assert.Equal(400, (await Send(setup.Panel, "POST", "/panel/api/content/drops", body.ToJsonString())).Status);
        }
        foreach (var field in new[] { "startsAt", "endsAt" })
        {
            var wrongType = CampaignDatesBody("2030-06-01", "2030-06-10");
            wrongType[field] = true;
            Assert.Equal(400, (await Send(setup.Panel, "POST", "/panel/api/content/campaigns", wrongType.ToJsonString())).Status);
            foreach (var value in new[] { "2030-06-02", "2030-06-02T19:00:00", "2030-02-30T19:00:00Z", "bad-date" })
            {
                var body = CampaignDropDatesBody(campaign, "2030-06-02", "2030-06-04");
                body[field] = value;
                Assert.Equal(400, (await Send(setup.Panel, "POST", "/panel/api/content/drops", body.ToJsonString())).Status);
                var campaignBody = CampaignDatesBody("2030-06-01", "2030-06-10");
                campaignBody[field] = value;
                Assert.Equal(400, (await Send(setup.Panel, "POST", "/panel/api/content/campaigns", campaignBody.ToJsonString())).Status);
            }
        }
        foreach (var dates in new[] { ("2030-06-01", "2030-06-01"), ("2030-06-02", "2030-06-01") })
            Assert.Equal(400, (await Send(setup.Panel, "POST", "/panel/api/content/campaigns", CampaignDatesBody(dates.Item1, dates.Item2).ToJsonString())).Status);
        var missingEnd = CampaignDatesBody("2030-06-01", "2030-06-10");
        missingEnd.Remove("endsAt");
        Assert.Equal(400, (await Send(setup.Panel, "POST", "/panel/api/content/campaigns", missingEnd.ToJsonString())).Status);
        var overview = await Send(setup.Panel, "GET", "/panel/api/content/schedule");
        Assert.Single(overview.Json!["campaigns"]!.AsArray());
        Assert.Empty(overview.Json["drops"]!.AsArray());
        var boundary = await Send(setup.Panel, "POST", "/panel/api/content/drops", CampaignDropDatesBody(campaign, "2030-06-01", "2030-06-10").ToJsonString());
        Assert.True(boundary.Status == 200, boundary.Body);
    }

    [Fact]
    public async Task CampaignDateEditsCannotExcludeExistingDropsOrUseStaleVersion()
    {
        using var setup = await CreateAsync();
        var created = await Send(setup.Panel, "POST", "/panel/api/content/campaigns", CampaignDatesBody("2030-06-01", "2030-06-10").ToJsonString());
        Assert.True(created.Status == 200, created.Body);
        var campaign = created.Json!;
        var drop = await Send(setup.Panel, "POST", "/panel/api/content/drops", CampaignDropDatesBody(campaign, "2030-06-02", "2030-06-08").ToJsonString());
        Assert.True(drop.Status == 200, drop.Body);
        var path = $"/panel/api/content/campaigns/{campaign["id"]!.GetValue<long>()}";
        foreach (var dates in new[] { ("2030-06-03", "2030-06-10"), ("2030-06-01", "2030-06-07") })
        {
            var body = CampaignDatesBody(dates.Item1, dates.Item2);
            body["version"] = campaign["version"]!.DeepClone();
            Assert.Equal(400, (await Send(setup.Panel, "PUT", path, body.ToJsonString())).Status);
        }
        var allowed = CampaignDatesBody("2030-06-02", "2030-06-08");
        allowed["version"] = campaign["version"]!.DeepClone();
        var changed = await Send(setup.Panel, "PUT", path, allowed.ToJsonString());
        Assert.True(changed.Status == 200, changed.Body);
        Assert.Equal(CampaignDateUtc("2030-06-02", 18), CampaignTimestamp(changed.Json!, "startsAt"));
        Assert.Equal(CampaignDateUtc("2030-06-08", 18), CampaignTimestamp(changed.Json!, "endsAt"));
        Assert.Equal(409, (await Send(setup.Panel, "PUT", path, allowed.ToJsonString())).Status);
        var overview = await Send(setup.Panel, "GET", "/panel/api/content/schedule");
        var preserved = Assert.Single(overview.Json!["drops"]!.AsArray())!;
        Assert.Equal(CampaignTimestamp(drop.Json!, "startsAt"), CampaignTimestamp(preserved, "startsAt"));
        Assert.Equal(CampaignTimestamp(drop.Json!, "endsAt"), CampaignTimestamp(preserved, "endsAt"));
    }

    private static JsonObject CampaignDatesBody(string start, string end) => new()
    {
        ["name"] = "Synthetic campaign date window", ["startsAt"] = CampaignEventsInstant(start), ["endsAt"] = CampaignEventsInstant(end)
    };

    private static JsonObject CampaignDropDatesBody(JsonNode campaign, string start, string end) => new()
    {
        ["campaignId"] = campaign["id"]!.DeepClone(), ["name"] = "Synthetic nested date window",
        ["startsAt"] = CampaignEventsInstant(start), ["endsAt"] = CampaignEventsInstant(end), ["inheritEnd"] = false
    };

    // Known January/June fixtures sent as the existing Events client would: 19:00 in London.
    private static string CampaignEventsInstant(string date) => date + (date.Substring(5, 2) == "01" ? "T19:00:00Z" : "T19:00:00+01:00");
    private static DateTimeOffset CampaignTimestamp(JsonNode row, string field) => DateTimeOffset.Parse(row[field]!.GetValue<string>(), CultureInfo.InvariantCulture);
    private static DateTimeOffset CampaignDateUtc(string date, int hour) => new(DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddHours(hour), TimeSpan.Zero);
}
