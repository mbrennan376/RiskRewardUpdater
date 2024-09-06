using System.Text.Json.Serialization;

namespace RiskRewardUpdater.Entities
{
    public class BestMatch
    {
        [JsonPropertyName("2. name")]
        public string Name { get; set; }
    }
}
