using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace AIAssistant
{
    /// <summary>
    /// Stores user-customizable system prompts for each code action.
    /// Persists to %APPDATA%/AIAssistant/prompts.json with UTF-8 encoding (Korean support).
    /// </summary>
    [DataContract]
    public class PromptConfig
    {
        private static PromptConfig _instance;
        private static readonly object _lock = new object();

        [DataMember(Name = "reviewPrompt")]
        public string ReviewPrompt { get; set; }

        [DataMember(Name = "explainPrompt")]
        public string ExplainPrompt { get; set; }

        [DataMember(Name = "optimizePrompt")]
        public string OptimizePrompt { get; set; }

        [DataMember(Name = "refactorPrompt")]
        public string RefactorPrompt { get; set; }

        [DataMember(Name = "generatePrompt")]
        public string GeneratePrompt { get; set; }

        public static PromptConfig Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = Load();
                        }
                    }
                }
                return _instance;
            }
        }

        public PromptConfig()
        {
            ResetToDefaults();
        }

        public void ResetToDefaults()
        {
            ReviewPrompt = @"You are an expert code reviewer. Review the following code thoroughly.
Provide:
1. Potential bugs or issues
2. Code quality improvements
3. Performance suggestions
4. Security concerns (if any)
5. Best practice violations

Be concise but thorough. Use bullet points.";

            ExplainPrompt = @"You are an expert programmer. Explain the following code in detail.
Provide:
1. Overall purpose of the code
2. Step-by-step breakdown of the logic
3. Key data structures and algorithms used
4. Any notable patterns or techniques

Be clear and educational.";

            OptimizePrompt = @"You are a performance optimization expert. Optimize the following code.
Provide:
1. The optimized version of the code
2. Explanation of each optimization
3. Expected performance improvement

Return the complete optimized code in a code block, followed by explanations.";

            RefactorPrompt = @"You are a refactoring expert. Refactor the following code for better readability, maintainability, and adherence to SOLID principles.
Provide:
1. The refactored code
2. Explanation of refactoring decisions
3. Design patterns applied (if any)

Return the complete refactored code in a code block, followed by explanations.";

            GeneratePrompt = @"You are an expert code generator. Generate clean, well-documented code based on the user's description.
Follow best practices for the language. Include comments. Return only the code in a code block.";
        }

        #region Save / Load

        private static string GetConfigPath()
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AIAssistant");
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            return Path.Combine(folder, "prompts.json");
        }

        public void Save()
        {
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(PromptConfig));
                using (var ms = new MemoryStream())
                {
                    serializer.WriteObject(ms, this);
                    string json = Encoding.UTF8.GetString(ms.ToArray());
                    File.WriteAllText(GetConfigPath(), json, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AIAssistant] PromptConfig save error: {ex.Message}");
            }
        }

        public static PromptConfig Load()
        {
            try
            {
                string path = GetConfigPath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    var serializer = new DataContractJsonSerializer(typeof(PromptConfig));
                    using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    {
                        var config = serializer.ReadObject(ms) as PromptConfig;
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
                System.Diagnostics.Debug.WriteLine($"[AIAssistant] PromptConfig load error: {ex.Message}");
            }

            var defaultConfig = new PromptConfig();
            _instance = defaultConfig;
            return defaultConfig;
        }

        #endregion
    }
}
