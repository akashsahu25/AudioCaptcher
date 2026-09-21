using AudioStreaming.Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace AudioStreaming.Backend.Controllers;

[ApiController]
[Route("api/audio/sessions/{sessionId:guid}/chunks")]
public sealed class AudioChunksController(AudioCaptureService capture, IWebHostEnvironment environment) : ControllerBase
{
    private string Folder(Guid id) => Path.Combine(environment.ContentRootPath, "Output", "Recordings", id.ToString("N"));

    [HttpGet]
    public IActionResult List(Guid sessionId)
    {
        if (capture.GetStatus(sessionId) is null) return NotFound();
        var folder = Folder(sessionId);
        var chunks = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "chunk-*.webm").Select(path => new FileInfo(path))
                .Select(file => new { File = file, Sequence = ParseSequence(file.Name) })
                .Where(item => item.Sequence > 0).OrderBy(item => item.Sequence)
                .Select(item => new { sessionId, sequence = item.Sequence, bytes = item.File.Length,
                    contentType = WebmOpusMuxer.ContentType,
                    downloadPath = $"/api/audio/sessions/{sessionId}/chunks/{item.Sequence}" }).ToArray()
            : [];
        return Ok(chunks);
    }

    [HttpGet("{sequence:int:min(1)}")]
    public IActionResult Download(Guid sessionId, int sequence)
    {
        if (capture.GetStatus(sessionId) is null) return NotFound();
        var path = Path.Combine(Folder(sessionId), $"chunk-{sequence:D6}.webm");
        return System.IO.File.Exists(path)
            ? PhysicalFile(path, WebmOpusMuxer.ContentType, enableRangeProcessing: true)
            : NotFound();
    }

    private static int ParseSequence(string name) =>
        int.TryParse(Path.GetFileNameWithoutExtension(name).AsSpan("chunk-".Length), out var value) ? value : 0;
}
