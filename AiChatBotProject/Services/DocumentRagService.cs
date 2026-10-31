using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiChatBotProject.Models;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Data.Sqlite;
using UglyToad.PdfPig;

namespace AiChatBotProject.Services;

public sealed class DocumentRagService
{
    private const long MaximumFileSize = 10 * 1024 * 1024;
    private const int MaximumChunksPerDocument = 500;
    private const int ChunkSize = 1400;
    private const int ChunkOverlap = 180;
    private const int EmbeddingBatchSize = 32;
    private const int MaximumRetrievedChunks = 5;
    private const int MaximumExtractedCharacters = MaximumChunksPerDocument * ChunkSize;
    private const float MinimumSimilarity = 0.15f;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
        ".docx",
        ".txt"
    };

    private readonly string _storageDirectory;
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly DeepSeekService _ai;
    private readonly ILogger<DocumentRagService> _logger;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    public DocumentRagService(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        DeepSeekService ai,
        ILogger<DocumentRagService> logger)
    {
        var configuredRoot = configuration["DocumentStore:RootPath"] ?? "App_Data";
        var root = Path.IsPathRooted(configuredRoot)
            ? configuredRoot
            : Path.Combine(environment.ContentRootPath, configuredRoot);

        _storageDirectory = Path.Combine(root, "documents");
        _databasePath = Path.Combine(root, "documents.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            ForeignKeys = true,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        _ai = ai;
        _logger = logger;
    }

    public async Task<DocumentSummary> AddAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length is <= 0 or > MaximumFileSize)
        {
            throw new InvalidDataException("Choose a non-empty document no larger than 10 MB.");
        }

        var fileName = Path.GetFileName(file.FileName).Trim();
        var extension = Path.GetExtension(fileName);
        if (!AllowedExtensions.Contains(extension))
        {
            throw new InvalidDataException("Only PDF, DOCX, and TXT documents are supported.");
        }

        await EnsureInitializedAsync(cancellationToken);
        Directory.CreateDirectory(_storageDirectory);

        var id = Guid.NewGuid().ToString("N");
        var storedName = id + extension.ToLowerInvariant();
        var storedPath = Path.Combine(_storageDirectory, storedName);
        var metadataCreated = false;
        try
        {
            await using (var output = new FileStream(
                             storedPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await file.CopyToAsync(output, cancellationToken);
            }

            var hash = await ComputeHashAsync(storedPath, cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using (var duplicateCheck = connection.CreateCommand())
            {
                duplicateCheck.CommandText = "SELECT 1 FROM documents WHERE sha256 = $sha256 LIMIT 1;";
                duplicateCheck.Parameters.AddWithValue("$sha256", hash);
                if (await duplicateCheck.ExecuteScalarAsync(cancellationToken) is not null)
                {
                    throw new DuplicateDocumentException();
                }
            }

            var uploadedAt = DateTimeOffset.UtcNow;
            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO documents (id, file_name, stored_name, sha256, status, error, uploaded_at)
                    VALUES ($id, $file_name, $stored_name, $sha256, 'Processing', NULL, $uploaded_at);
                    """;
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$file_name", LimitFileName(fileName));
                insert.Parameters.AddWithValue("$stored_name", storedName);
                insert.Parameters.AddWithValue("$sha256", hash);
                insert.Parameters.AddWithValue("$uploaded_at", uploadedAt.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken);
                metadataCreated = true;
            }

            return await ProcessAsync(id, cancellationToken);
        }
        catch
        {
            if (!metadataCreated)
            {
                File.Delete(storedPath);
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<DocumentSummary>> ListAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, file_name, status, error, uploaded_at
            FROM documents
            ORDER BY uploaded_at DESC;
            """;

        var documents = new List<DocumentSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            documents.Add(new DocumentSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4))));
        }

        return documents;
    }

    public async Task<DocumentSummary?> GetAsync(string id, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, file_name, status, error, uploaded_at
            FROM documents
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DocumentSummary(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4)));
    }

    public async Task<DocumentSummary?> ReindexAsync(string id, CancellationToken cancellationToken)
    {
        var document = await GetStoredDocumentAsync(id, cancellationToken);
        return document is null ? null : await ProcessAsync(id, cancellationToken);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        var document = await GetStoredDocumentAsync(id, cancellationToken);
        if (document is null)
        {
            return false;
        }

        var storedPath = Path.Combine(_storageDirectory, document.Value.StoredName);
        if (File.Exists(storedPath))
        {
            File.Delete(storedPath);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM documents WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<DocumentSearchResult> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var rows = new List<(string Content, int? PageNumber, float[] Embedding, string FileName)>();
        await using (var connection = await OpenConnectionAsync(cancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT c.content, c.page_number, c.embedding, d.file_name
                FROM chunks c
                INNER JOIN documents d ON d.id = c.document_id
                WHERE d.status = 'Ready';
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(ReadCandidate(reader));
            }
        }

        if (rows.Count == 0)
        {
            return new DocumentSearchResult(false, Array.Empty<RetrievedChunk>());
        }

        var queryEmbedding = (await _ai.EmbedAsync(new[] { query }, cancellationToken))[0];
        var candidates = new List<RetrievedChunk>();
        foreach (var row in rows)
        {
            var score = CosineSimilarity(queryEmbedding, row.Embedding);
            if (score < MinimumSimilarity)
            {
                continue;
            }

            candidates.Add(new RetrievedChunk(row.FileName, row.PageNumber, row.Content, score));
        }

        var bestMatches = candidates
            .OrderByDescending(candidate => candidate.Score)
            .Take(MaximumRetrievedChunks)
            .ToArray();
        return new DocumentSearchResult(true, bestMatches);
    }

    private async Task<DocumentSummary> ProcessAsync(string id, CancellationToken cancellationToken)
    {
        var document = await GetStoredDocumentAsync(id, cancellationToken)
                       ?? throw new InvalidOperationException("The document no longer exists.");
        var storedPath = Path.Combine(_storageDirectory, document.StoredName);

        try
        {
            await SetStatusAsync(id, "Processing", null, cancellationToken);
            var pages = await ExtractTextAsync(storedPath, cancellationToken);
            if (pages.Sum(page => (long)page.Text.Length) > MaximumExtractedCharacters)
            {
                throw new InvalidDataException("The document contains too much text to index (maximum 500 chunks).");
            }

            var chunks = CreateChunks(pages);
            if (chunks.Count == 0)
            {
                throw new InvalidDataException("No readable text was found in the document.");
            }

            if (chunks.Count > MaximumChunksPerDocument)
            {
                throw new InvalidDataException("The document contains too much text to index (maximum 500 chunks).");
            }

            var embeddings = new List<float[]>(chunks.Count);
            for (var offset = 0; offset < chunks.Count; offset += EmbeddingBatchSize)
            {
                var batch = chunks.Skip(offset).Take(EmbeddingBatchSize).ToArray();
                embeddings.AddRange(await _ai.EmbedAsync(
                    batch.Select(chunk => chunk.Content).ToArray(),
                    cancellationToken));
            }

            await SaveChunksAsync(id, chunks, embeddings, cancellationToken);
            return await GetAsync(id, cancellationToken)
                   ?? throw new InvalidOperationException("The indexed document could not be loaded.");
        }
        catch (OperationCanceledException)
        {
            await SetStatusAsync(id, "Failed", "Indexing was interrupted; retry indexing.", CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            var message = exception is InvalidDataException
                ? exception.Message
                : "Indexing failed. Check the provider configuration and retry.";
            await SetStatusAsync(id, "Failed", message, CancellationToken.None);
            _logger.LogWarning(exception, "Indexing failed for uploaded document {DocumentId}", id);
            if (exception is InvalidDataException)
            {
                throw;
            }

            throw new DocumentIndexingException(exception);
        }
    }

    private async Task<IReadOnlyList<ExtractedPage>> ExtractTextAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return await Task.Run<IReadOnlyList<ExtractedPage>>(() =>
            {
                using var pdf = PdfDocument.Open(path);
                return pdf.GetPages()
                    .Select(page => new ExtractedPage(page.Text, page.Number))
                    .ToArray();
            }, cancellationToken);
        }

        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            return await Task.Run<IReadOnlyList<ExtractedPage>>(() =>
            {
                using var document = WordprocessingDocument.Open(
                    path,
                    false,
                    new OpenSettings { MaxCharactersInPart = MaximumExtractedCharacters });
                var text = string.Join(
                    Environment.NewLine,
                    document.MainDocumentPart?.Document.Body?
                        .Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>()
                        .Select(paragraph => paragraph.InnerText)
                        ?? Enumerable.Empty<string>());
                return new[] { new ExtractedPage(text, null) };
            }, cancellationToken);
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        var content = await reader.ReadToEndAsync(cancellationToken);
        return new[] { new ExtractedPage(content, null) };
    }

    private static List<Chunk> CreateChunks(IReadOnlyList<ExtractedPage> pages)
    {
        var chunks = new List<Chunk>();
        foreach (var page in pages)
        {
            var text = page.Text;
            var start = 0;
            while (start < text.Length)
            {
                while (start < text.Length && char.IsWhiteSpace(text[start]))
                {
                    start++;
                }

                if (start >= text.Length)
                {
                    break;
                }

                var end = Math.Min(start + ChunkSize, text.Length);
                if (end < text.Length)
                {
                    var whitespace = text.LastIndexOf(' ', end - 1, end - start);
                    if (whitespace > start + ChunkSize / 2)
                    {
                        end = whitespace;
                    }
                }

                var content = text[start..end].Trim();
                if (content.Length > 0)
                {
                    chunks.Add(new Chunk(chunks.Count, page.PageNumber, content));
                }

                if (end >= text.Length)
                {
                    break;
                }

                start = Math.Max(start + 1, end - ChunkOverlap);
            }
        }

        return chunks;
    }

    private async Task SaveChunksAsync(
        string documentId,
        IReadOnlyList<Chunk> chunks,
        IReadOnlyList<float[]> embeddings,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM chunks WHERE document_id = $id;";
            delete.Parameters.AddWithValue("$id", documentId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var index = 0; index < chunks.Count; index++)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO chunks (document_id, chunk_index, page_number, content, embedding)
                VALUES ($document_id, $chunk_index, $page_number, $content, $embedding);
                """;
            insert.Parameters.AddWithValue("$document_id", documentId);
            insert.Parameters.AddWithValue("$chunk_index", chunks[index].Index);
            object pageNumber = chunks[index].PageNumber.HasValue
                ? chunks[index].PageNumber.GetValueOrDefault()
                : DBNull.Value;
            insert.Parameters.AddWithValue("$page_number", pageNumber);
            insert.Parameters.AddWithValue("$content", chunks[index].Content);
            insert.Parameters.AddWithValue("$embedding", JsonSerializer.Serialize(embeddings[index]));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = "UPDATE documents SET status = 'Ready', error = NULL WHERE id = $id;";
            update.Parameters.AddWithValue("$id", documentId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task SetStatusAsync(
        string id,
        string status,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE documents SET status = $status, error = $error WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<(string FileName, string StoredName)?> GetStoredDocumentAsync(
        string id,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_name, stored_name FROM documents WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetString(0), reader.GetString(1))
            : null;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(_storageDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS documents (
                    id TEXT PRIMARY KEY,
                    file_name TEXT NOT NULL,
                    stored_name TEXT NOT NULL,
                    sha256 TEXT NOT NULL UNIQUE,
                    status TEXT NOT NULL,
                    error TEXT NULL,
                    uploaded_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS chunks (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    document_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
                    chunk_index INTEGER NOT NULL,
                    page_number INTEGER NULL,
                    content TEXT NOT NULL,
                    embedding TEXT NOT NULL,
                    UNIQUE (document_id, chunk_index)
                );
                CREATE INDEX IF NOT EXISTS ix_chunks_document_id ON chunks(document_id);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static (string Content, int? PageNumber, float[] Embedding, string FileName) ReadCandidate(
        SqliteDataReader reader)
    {
        return (
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            JsonSerializer.Deserialize<float[]>(reader.GetString(2))
                ?? throw new InvalidDataException("A stored document embedding is invalid."),
            reader.GetString(3));
    }

    private static float CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count != right.Count || left.Count == 0)
        {
            return 0;
        }

        double dot = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index];
            leftMagnitude += left[index] * left[index];
            rightMagnitude += right[index] * right[index];
        }

        return leftMagnitude == 0 || rightMagnitude == 0
            ? 0
            : (float)(dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude)));
    }

    private static string LimitFileName(string fileName)
    {
        return fileName.Length <= 200 ? fileName : fileName[^200..];
    }

    private sealed record ExtractedPage(string Text, int? PageNumber);
    private sealed record Chunk(int Index, int? PageNumber, string Content);
}

public sealed class DuplicateDocumentException : Exception
{
}

public sealed class DocumentIndexingException : Exception
{
    public DocumentIndexingException(Exception innerException)
        : base("Document indexing failed.", innerException)
    {
    }
}
