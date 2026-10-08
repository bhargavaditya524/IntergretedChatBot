using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
namespace AiChatBotProject.Services
{
    public class DeepSeekService 
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _config;

        public DeepSeekService (IConfiguration config)
        {
            _httpClient = new HttpClient ();
            _config = config;
        }

        public async Task<string> AskAi(string message)
        {
            var apiKey = _config["AiSettings:ApiKey"];
            var apiUrl = _config["AiSettings:ApiUrl"];

            var requestBody = new
            {
                model = "deepseek/deepseek-chat",
                messages = new[]
                {
                    new {role = "user", content = message}
                }
            };
            var request = new HttpRequestMessage(HttpMethod.Post, apiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            request.Content = new StringContent(
              JsonSerializer.Serialize(requestBody),
              Encoding.UTF8,
              "application/json");
            var response = await _httpClient.SendAsync(request);
            var result = await response.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(result);
            var aiResponse =
                json.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return aiResponse;
        }

    }
}
