using AiChatBotProject.Services;
using Microsoft.AspNetCore.Mvc;

namespace AiChatBotProject.Controllers;

[ApiController]
[Route("api/documents")]
public sealed class DocumentsController : ControllerBase
{
    private const long MaximumRequestSize = 10 * 1024 * 1024 + 128 * 1024;
    private readonly DocumentRagService _documents;

    public DocumentsController(DocumentRagService documents)
    {
        _documents = documents;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        return Ok(await _documents.ListAsync(cancellationToken));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken)
    {
        var document = await _documents.GetAsync(id, cancellationToken);
        return document is null ? NotFound() : Ok(document);
    }

    [HttpPost]
    [RequestSizeLimit(MaximumRequestSize)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaximumRequestSize)]
    public async Task<IActionResult> Upload(
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return BadRequest(new { error = "Choose a PDF, DOCX, or TXT file." });
        }

        try
        {
            var document = await _documents.AddAsync(file, cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = document.Id }, document);
        }
        catch (InvalidDataException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        catch (DuplicateDocumentException)
        {
            return Conflict(new { error = "This document is already in the knowledge base." });
        }
        catch (DocumentIndexingException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "Document indexing failed. Check the provider configuration and retry."
            });
        }
    }

    [HttpPost("{id}/reindex")]
    public async Task<IActionResult> Reindex(string id, CancellationToken cancellationToken)
    {
        try
        {
            var document = await _documents.ReindexAsync(id, cancellationToken);
            return document is null ? NotFound() : Ok(document);
        }
        catch (InvalidDataException exception)
        {
            return UnprocessableEntity(new { error = exception.Message });
        }
        catch (DocumentIndexingException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "Document indexing failed. Check the provider configuration and retry."
            });
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        return await _documents.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();
    }
}
