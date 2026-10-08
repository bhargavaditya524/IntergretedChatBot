# AiChatBotProject

A simple AI chatbot built with ASP.NET Core MVC. A browser chat page sends messages to a REST endpoint, which forwards them to the DeepSeek chat model through the [OpenRouter](https://openrouter.ai) API and returns the reply.

## Features

- Chat interface served from the MVC home page (`/`)
- `POST /api/chat` endpoint, testable from Swagger
- DeepSeek (`deepseek/deepseek-chat`) accessed through OpenRouter
- Typing indicator, error messages, light and dark mode
- Swagger UI for API testing

## Tech stack

- .NET (ASP.NET Core MVC + API controllers)
- C#
- OpenRouter Chat Completions API
- Vanilla HTML, CSS and JavaScript (no frontend framework)
- Swashbuckle (Swagger)

## Project structure

```
AiChatBotProject/
├── Controllers/
│   ├── ChatController.cs        # API: POST /api/chat
│   └── HomeController.cs        # Serves the chat page
├── Models/
│   ├── ChatRequest.cs           # Request body: { "message": "..." }
│   └── ErrorViewModel.cs
├── Services/
│   └── DeepSeekService.cs       # Calls the OpenRouter API
├── Views/
│   └── Home/
│       └── Index.cshtml         # Chat UI
├── appsettings.json             # Configuration (do NOT commit real keys)
└── Program.cs                   # App startup, DI, CORS, Swagger
```

## Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download) (the version your project targets)
- An OpenRouter account and API key: <https://openrouter.ai/keys>

## Setup

1. **Clone the repository** and open the project folder.

2. **Configure the API.** `appsettings.json` should contain only the URL and an empty key:

   ```json
   {
     "AiSettings": {
       "ApiKey": "",
       "ApiUrl": "https://openrouter.ai/api/v1/chat/completions"
     }
   }
   ```

3. **Provide your API key without committing it.**

   For local development, use user secrets:

   ```bash
   dotnet user-secrets init
   dotnet user-secrets set "AiSettings:ApiKey" "<your-openrouter-key>"
   ```

   For production, set an environment variable (note the double underscore):

   ```bash
   export AiSettings__ApiKey="<your-openrouter-key>"
   ```

4. **Run the app:**

   ```bash
   dotnet run
   ```

5. Open the URL printed in the console, for example `https://localhost:5001/`.

## Usage

### Chat page

Open `/` in a browser. Type a message and press **Enter** to send, or **Shift+Enter** for a new line. **Clear chat** resets the conversation.

### Swagger

Open `/swagger` to try the API interactively.

### API

**`POST /api/chat`**

Request:

```json
{ "message": "What is ASP.NET Core?" }
```

Response:

```json
{
  "user": "What is ASP.NET Core?",
  "ai": "ASP.NET Core is a cross-platform framework for building web apps and APIs..."
}
```

Example with curl:

```bash
curl -X POST https://localhost:5001/api/chat \
  -H "Content-Type: application/json" \
  -d '{"message":"Hello"}'
```

## How it works

1. The browser (`Index.cshtml`) sends the user's message to `/api/chat`.
2. `ChatController` passes it to `DeepSeekService.AskAi`.
3. `DeepSeekService` sends a chat completion request to OpenRouter with the API key as a Bearer token.
4. The reply text is read from `choices[0].message.content` and returned to the browser.

Each message is sent on its own, so the model does not remember earlier messages in the conversation.

## Changing the model

The model name is set in `Services/DeepSeekService.cs`:

```csharp
model = "deepseek/deepseek-chat"
```

Any model available on OpenRouter can be used by changing this value. See <https://openrouter.ai/models>.

## Troubleshooting

| Problem | Likely cause and fix |
|---|---|
| 500 error from `/api/chat` | OpenRouter returned an error (invalid key, no credits, rate limit) and the response has no `choices`. Check the key and your OpenRouter credits. |
| 401 or "No auth credentials" | `AiSettings:ApiKey` is empty or not loaded. Re-check user secrets or the environment variable. |
| Page shows "Could not get a reply" | The server is not running, or the API call failed. Check the console output. |
| Changes to `.cshtml` not showing | Stop and restart `dotnet run`, then hard refresh the browser (Ctrl+F5). |

## Security notes

- **Never commit API keys.** If a key was ever committed or shared, revoke it in OpenRouter and create a new one.
- CORS is currently set to allow any origin (`AllowAll`). Restrict it to your own domains before deploying.
- Swagger is enabled in all environments. Restrict it to development before deploying.
- There is no authentication on `/api/chat`. Anyone who can reach it can spend your OpenRouter credits, so add authentication and rate limiting before exposing it publicly.

## Roadmap

- Conversation history so the model remembers earlier messages
- Retrieval-augmented generation (RAG) so the bot answers from organization documents
- Streaming responses
- Authentication and per-user rate limiting
- Move `HttpClient` to `IHttpClientFactory` and add proper error handling in `DeepSeekService`
