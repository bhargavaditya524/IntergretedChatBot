using Microsoft.AspNetCore.Mvc;
using AiChatBotProject.Services;
using AiChatBotProject.Models;
using System.Net.Http;
using System.Text.Json;
namespace AiChatBotProject.Controllers
{
    [ApiController]
    [Route("api/chat")]
    public class ChatController : ControllerBase
    {
        private readonly DeepSeekService _service;
        private readonly DocumentRagService _documents;
        private readonly ILogger<ChatController> _logger;
       
        public ChatController(
            DeepSeekService service,
            DocumentRagService documents,
            ILogger<ChatController> logger)
        {
            _service = service;
            _documents = documents;
            _logger = logger;
        }
        [HttpPost]
        public async Task<IActionResult> Chat(ChatRequest request, CancellationToken cancellationToken)
        {
            try
            {
                var search = await _documents.SearchAsync(request.Message, cancellationToken);
                var response = await _service.AskAi(
                    request.Message,
                    search.Chunks,
                    search.HasIndexedDocuments,
                    cancellationToken);
                var sources = search.Chunks
                    .Select(chunk => (chunk.FileName, chunk.PageNumber))
                    .Distinct()
                    .Select((source, index) => new ChatSource(
                        $"[{index + 1}]",
                        source.FileName,
                        source.PageNumber))
                    .ToArray();
                return Ok(new
                {
                    user = request.Message,
                    ai = response,
                    sources
                });
            }
            catch (Exception exception) when (
                exception is HttpRequestException
                    or JsonException
                    or InvalidOperationException
                    or KeyNotFoundException)
            {
                _logger.LogWarning(exception, "Chat request failed while calling OpenRouter");
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    error = "OpenRouter request failed. Check the API configuration and retry."
                });
            }
        }

    }
}
