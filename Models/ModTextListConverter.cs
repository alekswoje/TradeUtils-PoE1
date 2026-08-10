using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TradeUtils.Models;

/// <summary>
/// Reads the trade API's mod arrays whether they hold plain strings or objects.
///
/// These used to be arrays of strings:
/// <code>"implicitMods": ["10% increased Cast Speed"]</code>
/// and are now arrays of objects carrying the text plus its hash and magnitudes:
/// <code>"implicitMods": [{"description": "10% increased Cast Speed", "domain": "implicit", ...}]</code>
///
/// Declaring them as <c>List&lt;string&gt;</c> meant every item that HAS mods failed to
/// deserialize, which threw away the whole ten-listing batch it arrived in. Items with no mods —
/// an unidentified jewel, say — kept working, so the damage looked like "this particular search is
/// broken" rather than "anything with an affix is unreadable".
///
/// Both shapes are accepted deliberately rather than just moving to the new one: this plugin talks
/// to a live API that has already changed this field once.
/// </summary>
public class ModTextListConverter : JsonConverter<List<string>>
{
    public override List<string> ReadJson(
        JsonReader reader,
        Type objectType,
        List<string> existingValue,
        bool hasExistingValue,
        JsonSerializer serializer)
    {
        var token = JToken.Load(reader);
        if (token == null || token.Type != JTokenType.Array) return null;

        var result = new List<string>();

        foreach (var entry in token)
        {
            switch (entry.Type)
            {
                case JTokenType.String:
                    result.Add(entry.ToString());
                    break;

                case JTokenType.Object:
                    // "description" is where the readable text moved to.
                    var text = entry["description"]?.ToString() ?? entry["name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(text)) result.Add(text);
                    break;

                // Anything else is a shape we don't know; skipping one entry beats losing the item.
            }
        }

        return result;
    }

    public override void WriteJson(JsonWriter writer, List<string> value, JsonSerializer serializer)
    {
        // Only ever deserialised, but round-tripping as plain strings keeps it well-behaved.
        writer.WriteStartArray();

        if (value != null)
            foreach (var text in value)
                writer.WriteValue(text);

        writer.WriteEndArray();
    }
}
