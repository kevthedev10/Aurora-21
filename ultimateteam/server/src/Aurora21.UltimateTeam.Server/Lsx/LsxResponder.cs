using System.Globalization;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core;
using Aurora21.UltimateTeam.Core.Services.Auth;

namespace Aurora21.UltimateTeam.Server.Lsx;

/// <summary>Answers decrypted LSX requests from Resources/lsx.json and the local profile.</summary>
public sealed class LsxResponder
{
    private readonly GameContext _game;
    private readonly ILogger _log;

    public LsxResponder(GameContext game, LsxSettings? settings = null)
    {
        _game = game;
        _log = game.Logger("UltimateTeam.Lsx");
        Settings = settings ?? LsxSettings.Load();
    }

    public LsxSettings Settings { get; }

    public string Respond(string requestXml)
    {
        var request = LsxXml.ParseRequest(requestXml);
        return LsxXml.Response(request.Id, Inner(request));
    }

    private string Inner(LsxXml.Request request)
    {
        switch (request.Name)
        {
            case "GetConfig":
                var services = Settings.Services.Select(service => LsxXml.Element("Service", new[] { ("Name", service[0]), ("Facility", service[1]) }));
                return $"<GetConfigResponse>{string.Concat(services)}</GetConfigResponse>";
            case "GetSetting":
                var setting = Settings.Settings.GetValueOrDefault(request.Attribute("SettingId"), string.Empty);
                return LsxXml.Element("GetSettingResponse", new[] { ("Setting", setting) });
            case "GetGameInfo":
                var gameInfoId = request.Attribute("GameInfoId").ToUpperInvariant();
                var gameInfo = Settings.GameInfo.GetValueOrDefault(gameInfoId, string.Empty);
                _log.LogInformation("LSX GetGameInfo {Id} -> {Value}", gameInfoId.Length > 0 ? gameInfoId : "<missing>", gameInfo);
                return LsxXml.Element("GetGameInfoResponse", new[] { ("GameInfo", gameInfo) });
        }
        if (Settings.Responses.TryGetValue(request.Name, out var response))
        {
            return LsxXml.Element(response, Values());
        }
        _log.LogWarning("LSX unmodelled request {Name}; returning generic ErrorSuccess", request.Name);
        return LsxXml.Element(Settings.UnmodelledResponse, Values());
    }

    /// <summary>Every value a configured response may name: settings, game info and the session.</summary>
    private (string Name, object Value)[] Values()
    {
        var config = _game.Config;
        var now = DateTime.UtcNow;
        return Settings.Settings.Concat(Settings.GameInfo)
            .Select(entry => (entry.Key, (object)entry.Value))
            .Concat(new (string, object)[]
            {
                ("nucleusId", config.NucleusId),
                ("personaId", config.PersonaId),
                ("persona", config.DisplayName),
                ("underAge", config.IsUnderAge(DateOnly.FromDateTime(DateTime.Now)) ? "true" : "false"),
                ("accessToken", LocalAuthService.AccessToken),
                ("authCode", LocalAuthService.AuthCode),
                ("utcTime", now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)),
                ("systemTime", now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
            })
            .ToArray();
    }
}
