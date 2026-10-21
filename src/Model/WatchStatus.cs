using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChasePie.Lookout.Model;

[JsonConverter(typeof(WatchStatusJsonConverter))]
public enum WatchStatus
{
    Pending,
    Healthy,
    Failing,
    Paused
}

public sealed class WatchStatusJsonConverter() : JsonStringEnumConverter<WatchStatus>(JsonNamingPolicy.CamelCase, false);
