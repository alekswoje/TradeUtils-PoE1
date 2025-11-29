using Newtonsoft.Json;

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


