using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace TotalBeasts.Api;

public static class PoeNinja
{
    private const string PoeNinjaUrlTemplate =
        "https://poe.ninja/poe1/api/economy/stash/current/item/overview?league={0}&type=Beast";

    private class PoeNinjaLine
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("chaosValue")] public float? ChaosValue;
    }

    private class PoeNinjaResponse
    {
        [JsonProperty("lines")] public List<PoeNinjaLine> Lines;
    }

    public static async Task<Dictionary<string, float>> GetBeastsPrices(string league)
    {
        using var httpClient = new HttpClient();
        var url = string.Format(PoeNinjaUrlTemplate, Uri.EscapeDataString(league));
        var response = await httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Failed to get poe.ninja response ({(int)response.StatusCode}) for league '{league}'");

        var json = await response.Content.ReadAsStringAsync();
        var poeNinjaResponse = JsonConvert.DeserializeObject<PoeNinjaResponse>(json);

        var prices = new Dictionary<string, float>();
        if (poeNinjaResponse?.Lines == null) return prices;

        foreach (var line in poeNinjaResponse.Lines)
        {
            if (string.IsNullOrEmpty(line?.Name) || line.ChaosValue == null) continue;
            prices[line.Name] = line.ChaosValue.Value;
        }

        return prices;
    }
}
