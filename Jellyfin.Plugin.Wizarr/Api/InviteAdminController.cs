using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Wizarr.Api;

/// <summary>
/// Admin-only bridge to Wizarr's REST API, used by the plugin's dashboard page.
/// </summary>
[ApiController]
[Route("InviteAdmin")]
[Authorize(Policy = "RequiresElevation")]
public class InviteAdminController : ControllerBase
{
    private readonly ILogger<InviteAdminController> _logger;

    public InviteAdminController(ILogger<InviteAdminController> logger)
    {
        _logger = logger;
    }

    [HttpGet("Status")]
    public Task<ContentResult> GetStatus(CancellationToken ct) => CallWizarr(HttpMethod.Get, "status", null, ct);

    [HttpGet("Servers")]
    public Task<ContentResult> GetServers(CancellationToken ct) => CallWizarr(HttpMethod.Get, "servers", null, ct);

    [HttpGet("Libraries")]
    public Task<ContentResult> GetLibraries(CancellationToken ct) => CallWizarr(HttpMethod.Get, "libraries", null, ct);

    [HttpGet("Invitations")]
    public Task<ContentResult> GetInvitations(CancellationToken ct) => CallWizarr(HttpMethod.Get, "invitations", null, ct);

    [HttpPost("Invitations")]
    public Task<ContentResult> CreateInvitation([FromBody] JsonElement body, CancellationToken ct)
        => CallWizarr(HttpMethod.Post, "invitations", body.GetRawText(), ct);

    [HttpDelete("Invitations/{id:int}")]
    public Task<ContentResult> DeleteInvitation([FromRoute] int id, CancellationToken ct)
        => CallWizarr(HttpMethod.Delete, $"invitations/{id}", null, ct);

    private async Task<ContentResult> CallWizarr(HttpMethod method, string path, string? body, CancellationToken ct)
    {
        var baseUri = WizarrConnection.BaseUri();
        var apiKey = WizarrConnection.ApiKey();
        if (baseUri is null || string.IsNullOrWhiteSpace(apiKey))
        {
            return Error(StatusCodes.Status400BadRequest, "Wizarr internal URL or API key is not configured.");
        }

        using var request = new HttpRequestMessage(method, new Uri(baseUri, "api/" + path));
        request.Headers.Add("X-API-Key", apiKey);
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        try
        {
            using var response = await WizarrConnection.Http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new ContentResult
            {
                StatusCode = (int)response.StatusCode,
                Content = text,
                ContentType = "application/json"
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not reach Wizarr at {Url}", baseUri);
            return Error(StatusCodes.Status502BadGateway, "Could not reach Wizarr. Check the internal URL and that both containers share a Docker network.");
        }
    }

    private static ContentResult Error(int statusCode, string error) => new()
    {
        StatusCode = statusCode,
        Content = JsonSerializer.Serialize(new { error }),
        ContentType = "application/json"
    };
}
