namespace WeredaConnect.Server.Models;

/// <summary>Order-lifecycle 2.26.0: <c>POST /admin/orders/{orderId}/commission</c>.</summary>
public sealed record CommissionChangeRequest
{
    /// <summary>The agreed rate as a fraction: 0 to 0.20, at most 4 decimals (<c>0.025</c> is 2.5 %).</summary>
    public decimal? CommissionRate { get; init; }

    /// <summary>Why, for the audit trail: 10 to 500 characters after trimming.</summary>
    public string? Reason { get; init; }

    /// <summary>The order's <c>version</c> as the admin's page read it.</summary>
    public int? ExpectedVersion { get; init; }
}

/// <summary>Order-lifecycle 2.26.0: the answer of a commission change, or of its replay (<see cref="AlreadyApplied"/>).</summary>
public sealed record CommissionChangeResponse
{
    public required string OrderId { get; init; }

    /// <summary>The order's status, unchanged (<c>created</c> or <c>pending_payment</c>).</summary>
    public string? Status { get; init; }

    public decimal CommissionRate { get; init; }

    public decimal CommissionAmount { get; init; }

    public decimal OrderTotal { get; init; }

    /// <summary>The rate before the change.</summary>
    public decimal? CommissionRateBefore { get; init; }

    /// <summary>The total before the change.</summary>
    public decimal? OrderTotalBefore { get; init; }

    public int Version { get; init; }

    /// <summary>The order already carried this change (same rate from the same version): nothing was written.</summary>
    public bool AlreadyApplied { get; init; }

    public required string Message { get; init; }
}
