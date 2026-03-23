using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace AIAssistant
{
    [DataContract]
    public class AssistantConfig
    {
        private static readonly string ConfigDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIAssistant");
        private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

        private static AssistantConfig _instance;

        [DataMember(Name = "apiUrl")]
        public string ApiUrl { get; set; } = "http://localhost:11434/v1/chat";

        [DataMember(Name = "model")]
        public string Model { get; set; } = "default";

        [DataMember(Name = "maxTokens")]
        public int MaxTokens { get; set; } = 4000;

        [DataMember(Name = "temperature")]
        public double Temperature { get; set; } = 0.7;

        [DataMember(Name = "timeoutSeconds")]
        public int TimeoutSeconds { get; set; } = 500;

        [DataMember(Name = "showThinking")]
        public bool ShowThinking { get; set; } = false;

        [DataMember(Name = "maxThinkingLength")]
        public int MaxThinkingLength { get; set; } = 5000;

        public static AssistantConfig Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = Load();
                }
                return _instance;
            }
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(ConfigDir))
                {
                    Directory.CreateDirectory(ConfigDir);
                }

                var serializer = new DataContractJsonSerializer(typeof(AssistantConfig),
                    new DataContractJsonSerializerSettings
                    {
                        UseSimpleDictionaryFormat = true
                    });

                using (var ms = new MemoryStream())
                {
                    serializer.WriteObject(ms, this);
                    string json = Encoding.UTF8.GetString(ms.ToArray());

                    // Pretty-print JSON manually
                    json = PrettyPrintJson(json);
                    File.WriteAllText(ConfigPath, json, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AIAssistant] Config save error: {ex.Message}");
            }
        }

        public static AssistantConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                    var serializer = new DataContractJsonSerializer(typeof(AssistantConfig));
                    using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    {
                        var config = (AssistantConfig)serializer.ReadObject(ms);
                        if (config != null)
                        {
                            _instance = config;
                            return config;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AIAssistant] Config load error: {ex.Message}");
            }

            var defaultConfig = new AssistantConfig();
            _instance = defaultConfig;
            return defaultConfig;
        }

        public void ResetToDefaults()
        {
            ApiUrl = "http://localhost:11434/v1/chat";
            Model = "default";
            MaxTokens = 4000;
            Temperature = 0.7;
            TimeoutSeconds = 500;
            ShowThinking = false;
            MaxThinkingLength = 5000;
        }

        private static string PrettyPrintJson(string json)
        {
            var sb = new StringBuilder();
            int indent = 0;
            bool inString = false;

            foreach (char c in json)
            {
                if (c == '"' && (sb.Length == 0 || sb[sb.Length - 1] != '\\'))
                {
                    inString = !inString;
                    sb.Append(c);
                }
                else if (!inString)
                {
                    switch (c)
                    {
                        case '{':
                        case '[':
                            sb.Append(c);
                            sb.AppendLine();
                            indent++;
                            sb.Append(new string(' ', indent * 2));
                            break;
                        case '}':
                        case ']':
                            sb.AppendLine();
                            indent--;
                            sb.Append(new string(' ', indent * 2));
                            sb.Append(c);
                            break;
                        case ',':
                            sb.Append(c);
                            sb.AppendLine();
                            sb.Append(new string(' ', indent * 2));
                            break;
                        case ':':
                            sb.Append(c);
                            sb.Append(' ');
                            break;
                        default:
                            if (!char.IsWhiteSpace(c))
                                sb.Append(c);
                            break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
