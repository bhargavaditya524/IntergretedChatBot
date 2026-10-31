using System.ComponentModel.DataAnnotations;

namespace AiChatBotProject.Models
{
    public class ChatRequest
    {
        [Required]
        [StringLength(4000)]
        public string Message { get; set; } = string.Empty;
    }
}
