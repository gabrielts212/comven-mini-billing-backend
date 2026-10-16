using Comven.Application.Abstractions;
using Comven.Application.Models;
using Microsoft.AspNetCore.Mvc;

namespace Comven.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class InvoicesController(IInvoiceBillingService invoiceBillingService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<InvoiceResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<InvoiceResponse>>> Get(
        [FromQuery] string? customerExternalId = null,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        CancellationToken cancellationToken = default)
    {
        var response = await invoiceBillingService.GetInvoicesAsync(
            new InvoiceFilter(customerExternalId, from, to),
            cancellationToken);

        return Ok(response);
    }

    [HttpPost("close")]
    [ProducesResponseType(typeof(InvoiceCloseResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<InvoiceCloseResponse>> Close(CancellationToken cancellationToken)
    {
        var response = await invoiceBillingService.CloseOpenInvoicesAsync(cancellationToken);
        return Ok(response);
    }
}