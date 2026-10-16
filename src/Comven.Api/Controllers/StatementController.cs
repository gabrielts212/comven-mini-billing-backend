using Comven.Application.Abstractions;
using Comven.Application.Models;
using Microsoft.AspNetCore.Mvc;

namespace Comven.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class StatementController(IBillingQueryService billingQueryService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(StatementOverviewResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<StatementOverviewResponse>> Get(
        [FromQuery] string customerExternalId = "cli-pre-001",
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        CancellationToken cancellationToken = default)
    {
        var response = await billingQueryService.GetStatementAsync(
            new StatementFilter(customerExternalId, from, to),
            cancellationToken);

        return Ok(response);
    }
}