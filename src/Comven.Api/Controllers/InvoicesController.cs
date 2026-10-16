using Comven.Application.Abstractions;
using Comven.Application.Models;
using Microsoft.AspNetCore.Mvc;

namespace Comven.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class InvoicesController(IInvoiceBillingService invoiceBillingService) : ControllerBase
{
    [HttpPost("close")]
    [ProducesResponseType(typeof(InvoiceCloseResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<InvoiceCloseResponse>> Close(CancellationToken cancellationToken)
    {
        var response = await invoiceBillingService.CloseOpenInvoicesAsync(cancellationToken);
        return Ok(response);
    }
}