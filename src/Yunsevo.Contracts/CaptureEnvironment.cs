namespace Yunsevo.Contracts;

public sealed record EnvironmentQuery(double Latitude, double Longitude, bool Address = true, bool Weather = true);
public sealed record WeatherReading(string Description, double TemperatureC, DateTimeOffset DataUtc, string Source,
    string SourceUrl, string Attribution, bool Forecast = false, double? Humidity = null, double? WindSpeed = null);
public sealed record CaptureEnvironment(string? Address, string? AddressSource, WeatherReading? Weather,
    DateTimeOffset RetrievedUtc, string? AddressAttribution = null, string? AddressError = null, string? WeatherError = null);
public sealed record EnvironmentSettings(bool Enabled = true, string AddressProvider = "photon", string WeatherProvider = "met-no",
    string PhotonServer = "https://photon.komoot.io", string Contact = "https://github.com/hnlyf168",
    int CacheMinutes = 10, bool HasAmapKey = false, string? AmapKey = null, bool ClearAmapKey = false);
