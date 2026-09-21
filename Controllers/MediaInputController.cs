using AudioStreaming.Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace AudioStreaming.Backend.Controllers;

[ApiController]
[Route("api/audio/media")]
public sealed class MediaInputController(MediaInputStore store) : ControllerBase
{
    [HttpGet]
    public IActionResult List()
    {
        try { return Ok(store.List()); }
        catch (InvalidOperationException error) { return Problem(error.Message, statusCode: 503); }
    }
}
