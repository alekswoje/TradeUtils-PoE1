using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TradeUtils.Models;

/// <summary>
/// Response model for the trade search API: /api/trade/search/{league}
/// We only care about the search id, result ids and total count.
/// </summary>
public class TradeSearchResponse
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("complexity")]
    public int Complexity { get; set; }

    [JsonProperty("result")]
    public string[] Result { get; set; }

    [JsonProperty("total")]
    public int Total { get; set; }
}

/// <summary>
/// Response model for GET /api/trade/search/{league}/{searchId} — the endpoint the trade site
/// itself uses to rehydrate a shared search link.
///
/// Note what it does NOT return: there is no <c>result</c> array here, only the stored query. So a
/// pasted trade URL costs two requests, not one — this GET to recover the query, then a normal POST
/// to run it. It answers unauthenticated, which is why the URL can be resolved before the user has
/// configured a POESESSID.
/// </summary>
public class SavedSearchResponse
{
    [JsonProperty("id")]
    public string Id { get; set; }

    /// <summary>
    /// The stored query object, kept raw so it round-trips into the POST body byte for byte.
    /// Deserialising it into typed filters would silently drop any filter this plugin does not
    /// model, which is the difference between buying what the user searched for and buying
    /// something adjacent to it.
    /// </summary>
    [JsonProperty("query")]
    public JObject Query { get; set; }
}


