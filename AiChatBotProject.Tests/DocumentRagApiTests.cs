using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AiChatBotProject.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AiChatBotProject.Tests;

public sealed class DocumentRagApiTests
{
    [Fact]
    public async Task Uploaded_document_is_retrieved_cited_and_deleted()
    {
        var root = CreateTemporaryRoot();
        try
        {
            using var factory = new TestFactory(root);
            using var client = factory.CreateClient();
            using var uploadContent = new MultipartFormDataContent();
            var file = new ByteArrayContent(Encoding.UTF8.GetBytes("Mars is known as the red planet."));
            file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            uploadContent.Add(file, "file", "facts.txt");

            using var uploadResponse = await client.PostAsync("/api/documents", uploadContent);
            Assert.Equal(HttpStatusCode.Created, uploadResponse.StatusCode);
            using var uploaded = JsonDocument.Parse(await uploadResponse.Content.ReadAsStringAsync());
            var id = uploaded.RootElement.GetProperty("id").GetString();
            Assert.NotNull(id);
            Assert.Equal("Ready", uploaded.RootElement.GetProperty("status").GetString());

            using var chatResponse = await client.PostAsJsonAsync("/api/chat", new
            {
                message = "What is Mars known as?"
            });
            Assert.Equal(HttpStatusCode.OK, chatResponse.StatusCode);
            using var chat = JsonDocument.Parse(await chatResponse.Content.ReadAsStringAsync());
            Assert.Contains("[1]", chat.RootElement.GetProperty("ai").GetString());
            var source = chat.RootElement.GetProperty("sources")[0];
            Assert.Equal("[1]", source.GetProperty("reference").GetString());
            Assert.Equal("facts.txt", source.GetProperty("fileName").GetString());

            using var deleteResponse = await client.DeleteAsync($"/api/documents/{id}");
            Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            using var list = JsonDocument.Parse(await client.GetStringAsync("/api/documents"));
            Assert.Empty(list.RootElement.EnumerateArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Duplicate_document_upload_is_rejected()
    {
        var root = CreateTemporaryRoot();
        try
        {
            using var factory = new TestFactory(root);
            using var client = factory.CreateClient();

            using var first = await UploadAsync(client, "duplicate.txt", Encoding.UTF8.GetBytes("same document"));
            using var duplicate = await UploadAsync(client, "duplicate.txt", Encoding.UTF8.GetBytes("same document"));

            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Pdf_and_docx_documents_are_accepted_and_indexed()
    {
        var root = CreateTemporaryRoot();
        try
        {
            using var factory = new TestFactory(root);
            using var client = factory.CreateClient();

            using var pdfResponse = await UploadAsync(client, "facts.pdf", CreatePdf("Jupiter is the largest planet."));
            using var docxResponse = await UploadAsync(client, "facts.docx", CreateDocx("Saturn has rings."));

            Assert.Equal(HttpStatusCode.Created, pdfResponse.StatusCode);
            Assert.Equal(HttpStatusCode.Created, docxResponse.StatusCode);
            using var pdf = JsonDocument.Parse(await pdfResponse.Content.ReadAsStringAsync());
            using var docx = JsonDocument.Parse(await docxResponse.Content.ReadAsStringAsync());
            Assert.Equal("Ready", pdf.RootElement.GetProperty("status").GetString());
            Assert.Equal("Ready", docx.RootElement.GetProperty("status").GetString());

            using var chatResponse = await client.PostAsJsonAsync("/api/chat", new
            {
                message = "Which planet is largest?"
            });
            using var chat = JsonDocument.Parse(await chatResponse.Content.ReadAsStringAsync());
            Assert.Contains("Jupiter", chat.RootElement.GetProperty("ai").GetString());
            Assert.Contains(
                chat.RootElement.GetProperty("sources").EnumerateArray(),
                source => source.GetProperty("fileName").GetString() == "facts.pdf"
                    && source.GetProperty("pageNumber").GetInt32() == 1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Chat_without_indexed_documents_still_uses_openrouter()
    {
        var root = CreateTemporaryRoot();
        try
        {
            using var factory = new TestFactory(root);
            using var client = factory.CreateClient();

            var home = await client.GetStringAsync("/");
            Assert.Contains("documentForm", home);

            using var response = await client.PostAsJsonAsync("/api/chat", new
            {
                message = "Say hello."
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("Hello.", json.RootElement.GetProperty("ai").GetString());
            Assert.Empty(json.RootElement.GetProperty("sources").EnumerateArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Failed_indexing_can_be_retried()
    {
        var root = CreateTemporaryRoot();
        try
        {
            using var factory = new TestFactory(root, failFirstEmbedding: true);
            using var client = factory.CreateClient();

            using var uploadResponse = await UploadAsync(
                client,
                "retry.txt",
                Encoding.UTF8.GetBytes("retry this document"));
            Assert.Equal(HttpStatusCode.BadGateway, uploadResponse.StatusCode);

            using var list = JsonDocument.Parse(await client.GetStringAsync("/api/documents"));
            var failedDocument = list.RootElement[0];
            Assert.Equal("Failed", failedDocument.GetProperty("status").GetString());
            var id = failedDocument.GetProperty("id").GetString();
            Assert.NotNull(id);

            using var retryResponse = await client.PostAsync($"/api/documents/{id}/reindex", null);
            Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
            using var retried = JsonDocument.Parse(await retryResponse.Content.ReadAsStringAsync());
            Assert.Equal("Ready", retried.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "rag-api-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string fileName, byte[] bytes)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(bytes), "file", fileName);
        return await client.PostAsync("/api/documents", content);
    }

    private static byte[] CreateDocx(string text)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(new Paragraph(new Run(new Text(text)))));
            mainPart.Document.Save();
        }

        return stream.ToArray();
    }

    private static byte[] CreatePdf(string text)
    {
        var content = $"BT /F1 18 Tf 72 720 Td ({text}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream"
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append($"{offset:D10} 00000 n \n");
        }

        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private sealed class TestFactory(
        string documentRoot,
        bool failFirstEmbedding = false) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiSettings:ApiKey"] = "test-key",
                    ["DocumentStore:RootPath"] = documentRoot
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DeepSeekService>();
                services.AddHttpClient<DeepSeekService>()
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeOpenRouterHandler(failFirstEmbedding));
            });
        }
    }

    private sealed class FakeOpenRouterHandler(bool failFirstEmbedding) : HttpMessageHandler
    {
        private int _failureTriggered;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/embeddings", StringComparison.Ordinal) == true)
            {
                if (failFirstEmbedding && Interlocked.Exchange(ref _failureTriggered, 1) == 0)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                using var body = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                var inputs = body.RootElement.GetProperty("input").EnumerateArray().ToArray();
                var data = inputs.Select((_, index) => new
                {
                    index,
                    embedding = new[] { 1f, 0f }
                });
                return JsonResponse(new { data });
            }

            using var chatRequest = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            var systemPrompt = chatRequest.RootElement
                .GetProperty("messages")[0]
                .GetProperty("content")
                .GetString() ?? string.Empty;
            var answer = systemPrompt.Contains("Jupiter is the largest planet.", StringComparison.Ordinal)
                ? "[1] Jupiter is the largest planet."
                : systemPrompt.Contains("Retrieved document excerpts:", StringComparison.Ordinal)
                    ? "[1] Mars is known as the red planet."
                    : "Hello.";
            return JsonResponse(new
            {
                choices = new[]
                {
                    new { message = new { content = answer } }
                }
            });
        }

        private static HttpResponseMessage JsonResponse<T>(T body)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
        }
    }
}
