using AuctionPlatform.Domain.Common;
using AuctionPlatform.Infrastructure.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers.Admin;

/// <summary>
/// The server's own health while it works: CPU, memory, thread pool, database connections, and
/// how long requests, connection hand-outs and auction locks take. Read-only. For watching a live
/// auction and for the bidding load test (tools/AuctionLoadTest), which reads it every second.
/// </summary>
[Route("api/v1/admin/diagnostics")]
[Authorize(Roles = Roles.SuperAdmin)]
public sealed class AdminDiagnosticsController : ApiControllerBase
{
    private readonly RuntimeMetrics _metrics;

    public AdminDiagnosticsController(RuntimeMetrics metrics) => _metrics = metrics;

    [HttpGet("runtime")]
    public ActionResult<RuntimeSnapshot> Runtime() => Ok(_metrics.Snapshot());
}
