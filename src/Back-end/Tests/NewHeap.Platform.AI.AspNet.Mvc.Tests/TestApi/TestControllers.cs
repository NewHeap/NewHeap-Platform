using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace NewHeap.Platform.AI.AspNet.Mvc.Tests.TestApi;

public enum TestOrderStatus
{
    Draft = 0,
    Confirmed = 1,
    Shipped = 2
}

public sealed class TestOrderCollectionRequest
{
    public int Page { get; set; } = 1;
    public int ItemsPerPage { get; set; } = 20;
    public string? Search { get; set; }
    public List<TestOrderBy> OrderBy { get; set; } = [];
    public List<TestFilter> Filter { get; set; } = [];
    public TestOrderStatus? Status { get; set; }
}

public sealed class TestOrderBy
{
    public string Key { get; set; } = string.Empty;
    public string Direction { get; set; } = "ASC";
}

public sealed class TestFilter
{
    public string Key { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;
    public string? Value { get; set; }
}

public sealed class TestOrderLine
{
    [Required]
    public string Sku { get; set; } = string.Empty;

    public int Quantity { get; set; }
}

public sealed class TestOrderInput
{
    [Required]
    public string Customer { get; set; } = string.Empty;

    public TestOrderStatus Status { get; set; }

    public DateTimeOffset? DeliverBy { get; set; }

    public List<TestOrderLine> Lines { get; set; } = [];
}

public sealed record TestOrder(int Id, string Customer, TestOrderStatus Status);

public sealed record TestEcho(
    string? Authorization,
    string? IdempotencyKey,
    string? Invocation,
    string? AcceptLanguage,
    string? Cookie,
    string? Query,
    string? Body);

/// <summary>Controller-level policy for orders; actions add stricter policies.</summary>
[ApiController]
[Route("orders")]
[Authorize(Policy = TestPolicies.OrderView)]
public sealed class OrderController : ControllerBase
{
    /// <summary>Lists orders with paging, search, ordering and filters.</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<TestOrder>> Get([FromQuery] TestOrderCollectionRequest request)
    {
        return Ok(new[] { new TestOrder(1, $"page-{request.Page}-{request.ItemsPerPage}-{request.Search}", request.Status ?? TestOrderStatus.Draft) });
    }

    [HttpGet("{id:int}")]
    [EndpointSummary("Get an order")]
    [EndpointDescription("Returns one order by identifier.")]
    public ActionResult<TestOrder> Get(int id)
    {
        if (id == 404)
        {
            return NotFound(new { secret = "not-found-body-text" });
        }
        return Ok(new TestOrder(id, "customer-" + id, TestOrderStatus.Confirmed));
    }

    [HttpGet("search")]
    public ActionResult<IReadOnlyList<TestOrder>> Search(
        [FromQuery] string text,
        [FromQuery] TestOrderStatus? status,
        [FromQuery] int limit = 10)
    {
        return Ok(new[] { new TestOrder(limit, text, status ?? TestOrderStatus.Draft) });
    }

    [HttpPost]
    [Authorize(Policy = TestPolicies.OrderManage)]
    public ActionResult<TestOrder> Create([FromBody] TestOrderInput input)
    {
        return Ok(new TestOrder(7, input.Customer, input.Status));
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = TestPolicies.OrderManage)]
    public ActionResult<TestOrder> Update(int id, [FromBody] TestOrderInput input)
    {
        return Ok(new TestOrder(id, input.Customer, input.Status));
    }

    [HttpDelete("{id:int}")]
    [Authorize(Policy = TestPolicies.OrderManage)]
    public IActionResult Delete(int id)
    {
        return NoContent();
    }

    [HttpGet("public")]
    [AllowAnonymous]
    public IActionResult Public()
    {
        return Ok();
    }

    [HttpPost("upload")]
    [Authorize(Policy = TestPolicies.OrderManage)]
    public IActionResult Upload(IFormFile file)
    {
        return Ok(file.Length);
    }

    [HttpGet("hidden")]
    [NhAiBridgeTool(Exclude = true)]
    public IActionResult Hidden()
    {
        return Ok();
    }

    [NonAction]
    public IActionResult Helper()
    {
        return Ok();
    }

    [HttpGet("described")]
    [NhAiBridgeTool(Description = "Attribute description wins.", MaxResultBytes = 2048, TimeoutSeconds = 5)]
    public IActionResult Described()
    {
        return Ok();
    }

    [HttpGet("approval-read")]
    [NhAiBridgeTool(RequireApproval = true)]
    public IActionResult ApprovalRead()
    {
        return Ok();
    }

    [HttpPost("notify")]
    [Authorize(Policy = TestPolicies.OrderManage)]
    [NhAiBridgeTool(Effect = NhAiToolEffect.ExternalSideEffect)]
    public IActionResult Notify()
    {
        return Ok();
    }
}

/// <summary>Actions that exercise execution, failure mapping and result bounds.</summary>
[ApiController]
[Route("probe")]
[Authorize(Policy = TestPolicies.OrderView)]
public sealed class ProbeController : ControllerBase
{
    [HttpGet("forbidden")]
    public IActionResult Forbidden()
    {
        return StatusCode(StatusCodes.Status403Forbidden, new { secret = "forbidden-body-text" });
    }

    [HttpGet("conflict")]
    public IActionResult Conflict()
    {
        return StatusCode(StatusCodes.Status409Conflict, new { secret = "conflict-body-text" });
    }

    [HttpGet("broken")]
    public IActionResult Broken()
    {
        return StatusCode(StatusCodes.Status500InternalServerError, new { secret = "broken-body-text" });
    }

    [HttpGet("large")]
    public IActionResult Large([FromQuery] int items = 2000)
    {
        return Ok(Enumerable.Range(0, items).Select(index => new { index, text = "large-\"quoted\"-é-" + index }).ToArray());
    }

    [HttpGet("slow")]
    [NhAiBridgeTool(TimeoutSeconds = 1)]
    public async Task<IActionResult> Slow(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        return Ok();
    }

    [HttpGet("echo")]
    public IActionResult EchoRead()
    {
        return Ok(Echo(null));
    }

    [HttpPost("echo")]
    [Authorize(Policy = TestPolicies.OrderManage)]
    public IActionResult EchoWrite([FromBody] TestOrderInput input)
    {
        return Ok(Echo(input.Customer));
    }

    [HttpPost("validate")]
    [Authorize(Policy = TestPolicies.OrderManage)]
    public IActionResult Validate([FromBody] TestOrderInput input)
    {
        ModelState.AddModelError(nameof(TestOrderInput.Customer), "Customer is not allowed.");
        return ValidationProblem(ModelState);
    }

    private TestEcho Echo(string? body)
    {
        var headers = Request.Headers;
        return new TestEcho(
            headers.Authorization.ToString(),
            headers["Idempotency-Key"].ToString(),
            headers["X-NewHeap-AI-Invocation"].ToString(),
            headers.AcceptLanguage.ToString(),
            headers.Cookie.ToString(),
            Request.QueryString.Value,
            body);
    }
}

/// <summary>Authorized without a named policy: excluded while explicit policies are required.</summary>
[ApiController]
[Route("unnamed")]
[Authorize]
public sealed class UnnamedPolicyController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        return Ok();
    }
}

/// <summary>Two actions that produce the same tool id.</summary>
[ApiController]
[Route("collision")]
[Authorize(Policy = TestPolicies.OrderView)]
public sealed class CollisionController : ControllerBase
{
    [HttpGet("first/{id:int}")]
    public IActionResult Get(int id)
    {
        return Ok(id);
    }

    [HttpGet("second/{id:int}")]
    [ActionName("Get")]
    public IActionResult GetSecond(int id)
    {
        return Ok(id);
    }
}

/// <summary>Read-only resource that only order managers may use.</summary>
[ApiController]
[Route("order-audits")]
[Authorize(Policy = TestPolicies.OrderManage)]
public sealed class OrderAuditController : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<TestOrder>> Get([FromQuery] TestOrderCollectionRequest request)
    {
        return Ok(new[] { new TestOrder(1, "audit", TestOrderStatus.Draft) });
    }

    [HttpGet("{id:int}")]
    public ActionResult<TestOrder> Get(int id)
    {
        return Ok(new TestOrder(id, "audit-" + id, TestOrderStatus.Draft));
    }
}

public sealed class TestLegacyOrderFilter
{
    public string? Region { get; set; }
}

/// <summary>
/// A list endpoint that reads page, itemsPerPage, search, orderBy and filter from the query
/// string itself, next to its own query model, like consumer base controllers do.
/// </summary>
[ApiController]
[Route("legacy-orders")]
[Authorize(Policy = TestPolicies.OrderView)]
public sealed class LegacyOrderController : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<TestOrder>> List([FromQuery] TestLegacyOrderFilter criteria)
    {
        var query = Request.Query;
        return Ok(new[]
        {
            new TestOrder(1, $"{criteria.Region}|{query["page"]}|{query["itemsPerPage"]}|{query["search"]}", TestOrderStatus.Draft)
        });
    }

    [HttpGet("{id:int}")]
    public ActionResult<TestOrder> Get(int id)
    {
        return Ok(new TestOrder(id, "legacy-" + id, TestOrderStatus.Draft));
    }
}

public static class TestPolicies
{
    public const string OrderView = "order.view";
    public const string OrderManage = "order.manage";
}
