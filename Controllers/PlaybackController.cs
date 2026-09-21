using AudioStreaming.Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace AudioStreaming.Backend.Controllers;

[ApiController]
[Route("api/audio/playback")]
public sealed class PlaybackController(PlaybackAutomationService playback,
    ILogger<PlaybackController> logger) : ControllerBase
{
    [HttpPost("start")]
    public async Task<IActionResult> Start(StartPlaybackRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(await playback.StartAsync(request.TargetKey, cancellationToken, request.MediaId)); }
        catch (PlaybackBusyException error) { return Conflict(new { message = error.Message }); }
        catch (DeviceBusyException error) { return Conflict(new { message = error.Message }); }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
        catch (Exception error)
        {
            logger.LogError(error, "Automated playback could not start");
            return Problem("Playback could not start. Check the configured local adapter and server logs.", statusCode: 503);
        }
    }
}
