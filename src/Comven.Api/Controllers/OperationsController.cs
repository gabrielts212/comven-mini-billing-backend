using Comven.Application.Abstractions;
using Comven.Application.Models;
using Microsoft.AspNetCore.Mvc;

namespace Comven.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class OperationsController(IBillingCommandService billingCommandService) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(CreateOperationResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CreateOperationResponse>> Post(
        [FromBody] CreateOperationRequest request,
        CancellationToken cancellationToken)
    {
        var response = await billingCommandService.CreateOperationAsync(request, cancellationToken);
        return Ok(response);
    }
}