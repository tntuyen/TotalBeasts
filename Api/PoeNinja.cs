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

    private class PoeNinjaItem
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string Name;
    }

    private class PoeNinjaLine
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string Name;
        [JsonProperty("primaryValue")] public float? PrimaryValue;
        [JsonProperty("chaosValue")] public float? ChaosValue;
    }

    private class PoeNinjaResponse
    {
        [JsonProperty("items")] public List<PoeNinjaItem> Items;
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

        // Newer poe.ninja responses can carry only an "id" per line with the
        // names in a separate "items" list, and the value in "primaryValue".
        var namesById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (poeNinjaResponse.Items != null)
        {
            foreach (var item in poeNinjaResponse.Items)
            {
                if (!string.IsNullOrEmpty(item?.Id) && !string.IsNullOrEmpty(item.Name))
                    namesById[item.Id] = item.Name;
            }
        }

        foreach (var line in poeNinjaResponse.Lines)
        {
            if (line == null) continue;

            var name = !string.IsNullOrEmpty(line.Id) && namesById.TryGetValue(line.Id, out var byId)
                ? byId
                : line.Name;
            var value = line.PrimaryValue > 0 ? line.PrimaryValue : line.ChaosValue;
            if (string.IsNullOrEmpty(name) || value == null) continue;

            prices[name] = value.Value;
        }

        return prices;
    }
}
