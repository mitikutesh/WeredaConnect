using System.Text.Json;
using FluentAssertions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// SIM-28: the fingerprint stored with an idempotency key names what the operation asks for, so a
/// resend matches it and a different payload under the same key does not.
/// </summary>
public class SyncPayloadFingerprintTests
{
    private static SyncOperationRequest Op(string dataJson, string action = "update", string entityId = "order-001",
        int? clientVersion = null, string operationId = "op-1", string? timestamp = null) => new()
        {
            OperationId = operationId,
            IdempotencyKey = "buyer-001:order:order-001:update:1",
            EntityType = "order",
            EntityId = entityId,
            Action = action,
            ClientVersion = clientVersion,
            Timestamp = timestamp,
            Data = JsonSerializer.Deserialize<Dictionary<string, object>>(dataJson)
        };

    [Fact]
    public void Of_IsLowercaseHexSha256()
    {
        SyncPayloadFingerprint.Of(Op("{\"status\":\"cancelled\"}")).Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Of_SamePayload_SameFingerprint_WhateverTheOperationIdAndTimestamp()
    {
        var first = Op("{\"status\":\"cancelled\",\"version\":2}", operationId: "op-1", timestamp: "2026-11-06T08:00:00Z");
        var resend = Op("{\"status\":\"cancelled\",\"version\":2}", operationId: "op-2", timestamp: "2026-11-06T09:00:00Z");

        SyncPayloadFingerprint.Of(resend).Should().Be(SyncPayloadFingerprint.Of(first));
    }

    [Fact]
    public void Of_KeyOrderDoesNotMatter_AtAnyDepth()
    {
        var a = Op("{\"status\":\"pickedUp\",\"gpsPickup\":{\"latitude\":9.03,\"longitude\":38.74},\"version\":2}");
        var b = Op("{\"version\":2,\"gpsPickup\":{\"longitude\":38.74,\"latitude\":9.03},\"status\":\"pickedUp\"}");

        SyncPayloadFingerprint.Of(b).Should().Be(SyncPayloadFingerprint.Of(a));
    }

    [Theory]
    [InlineData("18")]
    [InlineData("18.0")]
    [InlineData("1.8e1")]
    [InlineData("18.000")]
    public void Of_NumbersCompareByValue(string spelling)
    {
        SyncPayloadFingerprint.Of(Op($"{{\"quantity\":{spelling}}}"))
            .Should().Be(SyncPayloadFingerprint.Of(Op("{\"quantity\":18}")));
    }

    [Fact]
    public void Of_ValuesBuiltInCodeMatchTheSameValuesReadFromJson()
    {
        var fromJson = Op("{\"quantity\":50,\"pricePerKg\":45.5,\"photoIds\":[\"a\",\"b\"],\"paid\":true,\"note\":null}");
        var inCode = Op("{}") with
        {
            Data = new Dictionary<string, object>
            {
                ["quantity"] = 50,
                ["pricePerKg"] = 45.5,
                ["photoIds"] = new[] { "a", "b" },
                ["paid"] = true,
                ["note"] = null!
            }
        };

        SyncPayloadFingerprint.Of(inCode).Should().Be(SyncPayloadFingerprint.Of(fromJson));
    }

    [Theory]
    [InlineData("{\"status\":\"cancelled\",\"version\":3}")]
    [InlineData("{\"status\":\"accepted\",\"version\":2}")]
    [InlineData("{\"status\":\"cancelled\",\"version\":2,\"cancelReason\":\"price\"}")]
    [InlineData("{\"status\":\"cancelled\",\"version\":\"2\"}")]
    [InlineData("{\"status\":\"cancelled\"}")]
    public void Of_AnotherPayload_AnotherFingerprint(string otherData)
    {
        SyncPayloadFingerprint.Of(Op(otherData))
            .Should().NotBe(SyncPayloadFingerprint.Of(Op("{\"status\":\"cancelled\",\"version\":2}")));
    }

    [Fact]
    public void Of_AnotherEntityActionOrClientVersion_AnotherFingerprint()
    {
        const string data = "{\"status\":\"cancelled\"}";
        var baseline = SyncPayloadFingerprint.Of(Op(data));

        SyncPayloadFingerprint.Of(Op(data, entityId: "order-002")).Should().NotBe(baseline);
        SyncPayloadFingerprint.Of(Op(data, action: "delete")).Should().NotBe(baseline);
        SyncPayloadFingerprint.Of(Op(data, clientVersion: 2)).Should().NotBe(baseline);
    }

    [Fact]
    public void Of_NoData_IsStable()
    {
        var op = Op("{}") with { Data = null };

        SyncPayloadFingerprint.Of(op).Should().Be(SyncPayloadFingerprint.Of(op with { OperationId = "op-9" }));
    }
}
