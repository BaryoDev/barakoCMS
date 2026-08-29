using System.Text.Json;
using System.Text.Json.Serialization;

namespace BarakoCMS.Tests;

/// <summary>
/// The serializer settings a client of this API needs.
/// </summary>
/// <remarks>
/// The API sends enum names rather than numbers, so a client deserialising with plain
/// <c>JsonSerializerDefaults.Web</c> throws on any response carrying a status or a sensitivity
/// level. Tests read responses the way a client would, which means they need the same converter a
/// client needs.
///
/// That is the shape of the 4.0 break for anyone consuming this API, and it belongs somewhere
/// visible rather than duplicated at each call site.
/// </remarks>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
