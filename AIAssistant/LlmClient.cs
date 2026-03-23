using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AIAssistant
{
    /// <summary>
    /// Custom LLM HTTP client that sends requests to a /v1/chat endpoint.
    /// Matches the Python code_assistant payload format.
    /// </summary>
    public static class LlmClient
    {
        #region Request/Response Data Contracts

        [DataContract]
        private class ChatOptions
        {
            [DataMember(Name = "max_tokens")]
            public int MaxTokens { get; set; }

            [DataMember(Name = "temperature")]
            public double Temperature { get; set; }
        }

        [DataContract]
        private class ChatMessage
        {
            [DataMember(Name = "role")]
            public string Role { get; set; }

            [DataMember(Name = "content")]
            public string Content { get; set; }
        }

        [DataContract]
        private class ChatRequest
        {
            [DataMember(Name = "model")]
            public string Model { get; set; }

            [DataMember(Name = "messages")]
            public List<ChatMessage> Messages { get; set; }

            [DataMember(Name = "options")]
            public ChatOptions Options { get; set; }

            [DataMember(Name = "stream")]
            public bool Stream { get; set; }
        }

        // Ollama-style response
        [DataContract]
        private class OllamaMessage
        {
            [DataMember(Name = "content")]
            public string Content { get; set; }
        }

        [DataContract]
        private class OllamaResponse
        {
            [DataMember(Name = "message")]
            public OllamaMessage Message { get; set; }
        }

        // OpenAI-style response
        [DataContract]
        private class OpenAIChoice
        {
            [DataMember(Name = "message")]
            public OllamaMessage Message { get; set; }

            [DataMember(Name = "text")]
            public string Text { get; set; }
        }

        [DataContract]
        private class OpenAIResponse
        {
            [DataMember(Name = "choices")]
            public List<OpenAIChoice> Choices { get; set; }

            [DataMember(Name = "content")]
            public string Content { get; set; }
        }

        #endregion

        /// <summary>
        /// Send a chat request to the LLM server and return the response content.
        /// </summary>
        public static async Task<string> SendChatAsync(List<KeyValuePair<string, string>> messages,
            string model = null, int? maxTokens = null, double? temperature = null, int? timeoutSeconds = null)
        {
            var config = AssistantConfig.Instance;

            var chatMessages = new List<ChatMessage>();
            foreach (var kvp in messages)
            {
                chatMessages.Add(new ChatMessage { Role = kvp.Key, Content = kvp.Value });
            }

            var request = new ChatRequest
            {
                Model = model ?? config.Model,
                Messages = chatMessages,
                Options = new ChatOptions
                {
                    MaxTokens = maxTokens ?? config.MaxTokens,
                    Temperature = temperature ?? config.Temperature
                },
                Stream = false
            };

            string jsonPayload = SerializeToJson(request);
            int timeout = (timeoutSeconds ?? config.TimeoutSeconds) * 1000;

            return await Task.Run(() =>
            {
                try
                {
                    var httpRequest = (HttpWebRequest)WebRequest.Create(config.ApiUrl);
                    httpRequest.Method = "POST";
                    httpRequest.ContentType = "application/json; charset=utf-8";
                    httpRequest.Timeout = timeout;
                    httpRequest.ReadWriteTimeout = timeout;

                    byte[] payload = Encoding.UTF8.GetBytes(jsonPayload);
                    httpRequest.ContentLength = payload.Length;

                    using (var stream = httpRequest.GetRequestStream())
                    {
                        stream.Write(payload, 0, payload.Length);
                    }

                    using (var response = (HttpWebResponse)httpRequest.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        string responseBody = reader.ReadToEnd();
                        return ParseResponse(responseBody);
                    }
                }
                catch (WebException ex) when (ex.Status == WebExceptionStatus.Timeout)
                {
                    return $"Error: Request timed out after {timeoutSeconds ?? config.TimeoutSeconds}s";
                }
                catch (WebException ex) when (ex.Status == WebExceptionStatus.ConnectFailure)
                {
                    return "Error: Cannot connect to LLM server. Check your API URL in Settings.";
                }
                catch (WebException ex)
                {
                    string errorBody = "";
                    if (ex.Response != null)
                    {
                        using (var reader = new StreamReader(ex.Response.GetResponseStream()))
                        {
                            errorBody = reader.ReadToEnd();
                        }
                    }
                    return $"Error: HTTP request failed: {ex.Message}\n{errorBody}";
                }
                catch (Exception ex)
                {
                    return $"Error: {ex.Message}";
                }
            });
        }

        /// <summary>
        /// Convenience: single prompt with system message.
        /// </summary>
        public static async Task<string> AskAsync(string systemPrompt, string userContent)
        {
            var messages = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("system", systemPrompt),
                new KeyValuePair<string, string>("user", userContent)
            };
            string response = await SendChatAsync(messages);
            return ProcessThinkingBlocks(response);
        }

        /// <summary>
        /// Convenience: chat with full history.
        /// </summary>
        public static async Task<string> ChatAsync(List<KeyValuePair<string, string>> history)
        {
            string response = await SendChatAsync(history);
            return ProcessThinkingBlocks(response);
        }

        #region Response Parsing

        private static string ParseResponse(string responseBody)
        {
            // Try Ollama-style: { "message": { "content": "..." } }
            try
            {
                var ollamaResp = DeserializeFromJson<OllamaResponse>(responseBody);
                if (ollamaResp?.Message?.Content != null)
                {
                    return ollamaResp.Message.Content;
                }
            }
            catch { }

            // Try OpenAI-style: { "choices": [{ "message": { "content": "..." } }] }
            try
            {
                var openaiResp = DeserializeFromJson<OpenAIResponse>(responseBody);
                if (openaiResp?.Choices != null && openaiResp.Choices.Count > 0)
                {
                    var choice = openaiResp.Choices[0];
                    if (choice.Message?.Content != null)
                        return choice.Message.Content;
                    if (choice.Text != null)
                        return choice.Text;
                }
                if (openaiResp?.Content != null)
                {
                    return openaiResp.Content;
                }
            }
            catch { }

            // Fallback: return raw
            return responseBody;
        }

        #endregion

        #region Thinking Block Processing

        /// <summary>
        /// Process <think>...</think> blocks — same logic as Python code_assistant.
        /// </summary>
        public static string ProcessThinkingBlocks(string content)
        {
            if (string.IsNullOrEmpty(content) || !content.Contains("<think>"))
                return content;

            var config = AssistantConfig.Instance;

            if (!config.ShowThinking)
            {
                // Strip thinking blocks entirely
                return Regex.Replace(content, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();
            }

            // Show thinking but truncate
            return Regex.Replace(content, @"<think>(.*?)</think>", m =>
            {
                string inner = m.Groups[1].Value;
                if (inner.Length > config.MaxThinkingLength)
                {
                    inner = inner.Substring(0, config.MaxThinkingLength)
                        + $"\n... [Thinking truncated, {m.Groups[1].Value.Length - config.MaxThinkingLength} more chars] ...";
                }
                return $"<thinking>\n{inner}\n</thinking>";
            }, RegexOptions.Singleline);
        }

        #endregion

        #region JSON Helpers (DataContract-based, no extra NuGet needed)

        private static string SerializeToJson<T>(T obj)
        {
            var serializer = new DataContractJsonSerializer(typeof(T),
                new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            using (var ms = new MemoryStream())
            {
                serializer.WriteObject(ms, obj);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        private static T DeserializeFromJson<T>(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(T),
                new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (T)serializer.ReadObject(ms);
            }
        }

        #endregion

        #region Token Estimation

        /// <summary>
        /// Rough estimation: ~4 chars per token (English), ~2 chars per token (CJK).
        /// </summary>
        public static int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            // Simple heuristic
            return (int)Math.Ceiling(text.Length / 3.5);
        }

        /// <summary>
        /// Truncate history to fit within maxTokens, keeping system message + last N messages.
        /// </summary>
        public static List<KeyValuePair<string, string>> TruncateHistory(
            List<KeyValuePair<string, string>> history, int maxTokens = 8000)
        {
            int totalTokens = 0;
            foreach (var msg in history)
            {
                totalTokens += EstimateTokens(msg.Value);
            }

            if (totalTokens <= maxTokens)
                return history;

            var result = new List<KeyValuePair<string, string>>();

            // Always keep system messages
            for (int i = 0; i < history.Count; i++)
            {
                if (history[i].Key == "system")
                    result.Add(history[i]);
            }

            // Add messages from the end until we approach the limit
            int systemTokens = 0;
            foreach (var msg in result)
            {
                systemTokens += EstimateTokens(msg.Value);
            }

            int remaining = maxTokens - systemTokens;
            var recentMessages = new List<KeyValuePair<string, string>>();

            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (history[i].Key == "system") continue;

                int msgTokens = EstimateTokens(history[i].Value);
                if (remaining - msgTokens < 0 && recentMessages.Count > 0)
                    break;

                remaining -= msgTokens;
                recentMessages.Insert(0, history[i]);
            }

            result.AddRange(recentMessages);
            return result;
        }

        #endregion
    }
}
