using Microsoft.AspNetCore.Mvc;
using AiChatBotProject.Services;
using AiChatBotProject.Models;
namespace AiChatBotProject.Controllers
{
    [ApiController]
    [Route("api/chat")]
    public class ChatController : ControllerBase
    {
        private readonly DeepSeekService _service;
       
        public ChatController(DeepSeekService service)
        {
            _service = service;
        }
        [HttpPost]
        public async Task<IActionResult> Chat(ChatRequest request)
        {
            var response = await _service.AskAi(request.Message);
            return Ok(new
            {
            user = request.Message,
            ai = response
            });
        }

    }
}
