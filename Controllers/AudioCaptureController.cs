using AudioStreaming.Backend.Models;
using AudioStreaming.Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace AudioStreaming.Backend.Controllers;

[ApiController]
[Route("api/audio")]
public sealed class AudioCaptureController(AudioCaptureService capture) : ControllerBase
{
    [HttpGet("devices")]
    public IActionResult Devices() => Ok(capture.GetDevices());

    [HttpGet("processes")]
    public IActionResult Processes() => Ok(capture.GetProcesses());

    [HttpGet("sessions")]
    public IActionResult Sessions() => Ok(capture.GetSessions());

    [HttpGet("sessions/{id:guid}")]
    public IActionResult Status(Guid id) => capture.GetStatus(id) is { } status ? Ok(status) : NotFound();

    [HttpPost("start")]
    public async Task<IActionResult> Start(StartCaptureRequest request)
    {
        try { return Ok(await capture.StartCaptureAsync(request.ProcessId)); }
        catch (DeviceBusyException error) { return Conflict(new { message = error.Message }); }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
        catch (PlatformNotSupportedException error) { return Problem(error.Message, statusCode: 503); }
        catch (Exception)
        {
            return Problem("Capture could not start. Check the application PID, Windows audio availability, login session and server logs.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    [HttpPost("sessions/{id:guid}/stop")]
    public async Task<IActionResult> Stop(Guid id) =>
        await capture.StopCaptureAsync(id) is { } status ? Ok(status) : NotFound();
}

