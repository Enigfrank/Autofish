using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TShockAPI;

namespace AutoFishPlugin
{
    public class ConfigFile
    {
        [JsonPropertyName("入水后咬钩延迟Ticks(0为瞬间咬钩,60为1秒)")]
        public int BiteDelayTicks { get; set; } = 180;

        [JsonPropertyName("收杆后补抛延迟Ticks(20约0.33秒)")]
        public int RecastDelayTicks { get; set; } = 120;

        [JsonPropertyName("鱼饵消耗倍率(1.0为原版概率,0为不消耗,2.0为双倍消耗概率)")]
        public float BaitConsumeMultiplier { get; set; } = 1.0f;

        public static readonly string ConfigPath = Path.Combine(TShock.SavePath, "AutoFish.json");

        public static ConfigFile Read()
        {
            if (!File.Exists(ConfigPath))
            {
                var defaultConfig = new ConfigFile();
                defaultConfig.Write();
                return defaultConfig;
            }

            try
            {
                string json = File.ReadAllText(ConfigPath);
                var options = new JsonSerializerOptions
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };
                return JsonSerializer.Deserialize<ConfigFile>(json, options) ?? new ConfigFile();
            }
            catch
            {
                return new ConfigFile();
            }
        }

        public void Write()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            string json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(ConfigPath, json);
        }
    }
}