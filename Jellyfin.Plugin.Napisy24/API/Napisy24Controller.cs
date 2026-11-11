using System.Net.Mime;
using Jellyfin.Plugin.Napisy24.Activation;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Napisy24.API;

/// <summary>
/// Activation endpoints used by the plugin configuration page.
/// </summary>
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Authorize(Policy = Policies.SubtitleManagement)]
public class Napisy24Controller : ControllerBase
{
    private readonly ActivationService _activation;

    /// <summary>
    /// Initializes a new instance of the <see cref="Napisy24Controller"/> class.
    /// </summary>
    public Napisy24Controller(ActivationService activation)
    {
        _activation = activation;
    }

    /// <summary>
    /// Gets the activation status and the server id to bind a code to.
    /// </summary>
    [HttpGet("Napisy24/Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ActivationStatus> Status()
    {
        return Ok(_activation.GetStatus());
    }

    /// <summary>
    /// Validates and stores an activation code.
    /// </summary>
    [HttpPost("Napisy24/Activate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<ActivationStatus> Activate([FromBody] ActivationRequest? request)
    {
        var status = _activation.Activate(request?.Code);
        return status.IsActivated ? Ok(status) : BadRequest(status);
    }

    /// <summary>
    /// Removes the stored activation code.
    /// </summary>
    [HttpPost("Napisy24/Deactivate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ActivationStatus> Deactivate()
    {
        return Ok(_activation.Deactivate());
    }
}

/// <summary>
/// Activation request body.
/// </summary>
public sealed class ActivationRequest
{
    /// <summary>
    /// Gets or sets the activation code.
    /// </summary>
    public string? Code { get; set; }
}
