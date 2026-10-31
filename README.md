# Integrated Chatbot with Document RAG

This ASP.NET Core MVC app sends chat completions to OpenRouter and can ground responses in a shared local document library. This is retrieval-augmented generation (RAG), not model fine-tuning: uploaded files are extracted, chunked, embedded, and searched when a question is asked.

## Run

Set the OpenRouter API key outside source control, then start the app:

```powershell
$env:AiSettings__ApiKey = "your-openrouter-api-key"
dotnet run --project AiChatBotProject
```

The chat model, embedding model, and API URLs are configurable in `AiChatBotProject/appsettings.json`. Defaults use `deepseek/deepseek-chat` for chat and `openai/text-embedding-3-small` for embeddings. The configured OpenRouter key needs access to both endpoints.

## Documents

Use the **Knowledge base** section in the chat page to add PDF, DOCX, and UTF-8 TXT files (up to 10 MB each), retry failed indexing, reindex a document, or remove it. Reindex documents after changing the embedding model. PDF page numbers and filenames are returned as sources with answers. Scanned PDFs without selectable text are not OCR-processed.

SQLite metadata, chunk text, embeddings, and uploaded files are stored under `AiChatBotProject/App_Data` by default. Override `DocumentStore:RootPath` to move this persistent data directory; back it up with the app. A document with the same SHA-256 hash cannot be uploaded twice.

## API

- `POST /api/chat` — ask a question; response includes `ai` and a `sources` array.
- `GET /api/documents` — list document indexing states.
- `POST /api/documents` — multipart upload using the `file` field.
- `POST /api/documents/{id}/reindex` — retry indexing.
- `DELETE /api/documents/{id}` — remove the stored file, metadata, chunks, and embeddings.

Retrieved excerpts are sent to OpenRouter as part of the chat prompt. This app currently has no authentication and its knowledge base is shared; run it only in a trusted environment. Do not expose it publicly or upload sensitive documents unless access controls and your provider's data-handling terms are appropriate.
