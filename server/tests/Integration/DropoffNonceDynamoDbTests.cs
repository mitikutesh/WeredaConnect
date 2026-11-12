using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// ESC-35 against real DynamoDB (LocalStack in CI): the trucker's drop-off nonce is stored only next
/// to the farmer's nonce; a registration (the code made at pickup, or sent again when the QR opens)
/// never replaces a stored code, so a second device or a late queued registration cannot void the
/// code the buyer holds; only a renewal replaces it, restarting its freshness and the drop-off
/// misses; typed-code attempts on the drop-off leg are held by the trucker's nonce. Every conditional
/// write runs for real (a wrong expression name or condition fails here, not in a mock).
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DropoffNonceDynamoDbTests
{
    private readonly LocalDynamoDbFixture _db;
    private readonly string _run = Guid.NewGuid().ToString("N")[..10];

    public DropoffNonceDynamoDbTests(LocalDynamoDbFixture db) => _db = db;

    private DeliveryQrSecretRepository Repo => new(_db.Client, _db.QrSecrets);

    private string DeliveryId(string name) => $"del-esc35-{name}-{_run}";

    [LocalDynamoDbTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithoutTheFarmersNonce_NothingIsStored(bool renew)
    {
        var id = DeliveryId($"bare-{renew}");

        (await Repo.PutDropoffNonceAsync(id, "d.trucker-1", renew)).Should().Be(DropoffNonceOutcome.NoPickupNonce);

        var row = await _db.Client.GetItemAsync(new GetItemRequest
        {
            TableName = _db.QrSecrets,
            Key = new Dictionary<string, AttributeValue> { { "deliveryId", new AttributeValue { S = id } } },
            ConsistentRead = true
        });
        row.Item.Should().BeNullOrEmpty();
    }

    [LocalDynamoDbFact]
    public async Task TheTruckersNonce_StandsNextToTheFarmers()
    {
        var id = DeliveryId("stored");
        await Repo.PutNonceAsync(id, "farmer-1");

        (await Repo.PutDropoffNonceAsync(id, "d.trucker-1", renew: false)).Should().Be(DropoffNonceOutcome.Stored);

        var secret = await Repo.GetAsync(id);
        secret!.Nonce.Should().Be("farmer-1");
        secret.DropoffNonce.Should().Be("d.trucker-1");
        secret.DropoffIssuedAt.Should().NotBeNullOrEmpty();
        secret.IssuedAt.Should().NotBeNullOrEmpty();
    }

    [LocalDynamoDbFact]
    public async Task ASecondDevicesRegistration_LeavesTheFirstCode_ItsFreshnessAndMisses()
    {
        var id = DeliveryId("two-devices");
        await Repo.PutNonceAsync(id, "farmer-1");
        await Repo.PutDropoffNonceAsync(id, "d.phone-a", renew: false);
        await Repo.TryTakeCodeAttemptAsync(id, "delivered", "op-d1:aa", "d.phone-a", 5, dropoffNonce: true);
        var first = await Repo.GetAsync(id);

        (await Repo.PutDropoffNonceAsync(id, "d.phone-b", renew: false))
            .Should().Be(DropoffNonceOutcome.OtherCodeStored);

        var after = await Repo.GetAsync(id);
        after!.DropoffNonce.Should().Be("d.phone-a");
        after.DropoffIssuedAt.Should().Be(first!.DropoffIssuedAt);
        after.CodeMissesDelivered.Should().Be(1);
    }

    [LocalDynamoDbFact]
    public async Task ALateQueuedRegistration_DoesNotOverwriteARenewal()
    {
        var id = DeliveryId("late");
        await Repo.PutNonceAsync(id, "farmer-1");

        (await Repo.PutDropoffNonceAsync(id, "d.renewed", renew: true)).Should().Be(DropoffNonceOutcome.Stored);
        (await Repo.PutDropoffNonceAsync(id, "d.first", renew: false))
            .Should().Be(DropoffNonceOutcome.OtherCodeStored);

        (await Repo.GetAsync(id))!.DropoffNonce.Should().Be("d.renewed");
    }

    [LocalDynamoDbFact]
    public async Task TheSameNonceAgain_KeepsTheMisses_ARenewal_ClearsOnlyTheDropOffMisses()
    {
        var id = DeliveryId("renew");
        await Repo.PutNonceAsync(id, "farmer-1");
        await Repo.PutDropoffNonceAsync(id, "d.trucker-1", renew: false);
        (await Repo.TryTakeCodeAttemptAsync(id, "pickedUp", "op-p1:aa", "farmer-1", 5)).Outcome
            .Should().Be(CodeAttemptOutcome.Taken);
        (await Repo.TryTakeCodeAttemptAsync(id, "delivered", "op-d1:aa", "d.trucker-1", 5, dropoffNonce: true))
            .Should().Be(new CodeAttempt(CodeAttemptOutcome.Taken, 1));
        (await Repo.TryTakeCodeAttemptAsync(id, "delivered", "op-d2:bb", "d.trucker-1", 5, dropoffNonce: true))
            .Should().Be(new CodeAttempt(CodeAttemptOutcome.Taken, 2));
        var first = await Repo.GetAsync(id);

        // Opened again, and a renewal resent with the code already stored: nothing moves.
        foreach (var renew in new[] { false, true })
        {
            (await Repo.PutDropoffNonceAsync(id, "d.trucker-1", renew)).Should().Be(DropoffNonceOutcome.AlreadyStored);
            var again = await Repo.GetAsync(id);
            again!.CodeMissesDelivered.Should().Be(2);
            again.DropoffIssuedAt.Should().Be(first!.DropoffIssuedAt);
        }

        // The trucker's New drop-off code.
        await Task.Delay(5);
        (await Repo.PutDropoffNonceAsync(id, "d.trucker-2", renew: true)).Should().Be(DropoffNonceOutcome.Stored);
        var renewed = await Repo.GetAsync(id);
        renewed!.DropoffNonce.Should().Be("d.trucker-2");
        renewed.CodeMissesDelivered.Should().Be(0);
        renewed.CodeMissesPickedUp.Should().Be(1);
        renewed.Nonce.Should().Be("farmer-1");
        string.CompareOrdinal(renewed.DropoffIssuedAt, first!.DropoffIssuedAt).Should().BePositive();
    }

    [LocalDynamoDbFact]
    public async Task DropOffAttempts_AreHeldByTheTruckersNonce_AndLockAtTheCap()
    {
        var id = DeliveryId("lock");
        await Repo.PutNonceAsync(id, "farmer-1");
        await Repo.PutDropoffNonceAsync(id, "d.trucker-1", renew: false);

        // Checked against the farmer's nonce it is a changed nonce, never a counted miss.
        (await Repo.TryTakeCodeAttemptAsync(id, "delivered", "op-x:aa", "farmer-1", 5, dropoffNonce: true)).Outcome
            .Should().Be(CodeAttemptOutcome.NonceChanged);

        for (var i = 1; i <= 5; i++)
        {
            (await Repo.TryTakeCodeAttemptAsync(id, "delivered", $"op-{i}:aa", "d.trucker-1", 5, dropoffNonce: true))
                .Should().Be(new CodeAttempt(CodeAttemptOutcome.Taken, i));
        }

        (await Repo.TryTakeCodeAttemptAsync(id, "delivered", "op-6:aa", "d.trucker-1", 5, dropoffNonce: true)).Outcome
            .Should().Be(CodeAttemptOutcome.Locked);
        // A retry of an attempt already held still gets in (it is not a new guess).
        (await Repo.TryTakeCodeAttemptAsync(id, "delivered", "op-5:aa", "d.trucker-1", 5, dropoffNonce: true)).Outcome
            .Should().Be(CodeAttemptOutcome.Taken);

        // A registration does not unlock it; the trucker's renewal does.
        (await Repo.PutDropoffNonceAsync(id, "d.trucker-2", renew: false))
            .Should().Be(DropoffNonceOutcome.OtherCodeStored);
        (await Repo.GetAsync(id))!.CodeMissesDelivered.Should().Be(5);
        await Repo.PutDropoffNonceAsync(id, "d.trucker-2", renew: true);
        (await Repo.TryTakeCodeAttemptAsync(id, "delivered", "op-7:aa", "d.trucker-1", 5, dropoffNonce: true)).Outcome
            .Should().Be(CodeAttemptOutcome.NonceChanged);
        (await Repo.TryTakeCodeAttemptAsync(id, "delivered", "op-7:aa", "d.trucker-2", 5, dropoffNonce: true))
            .Should().Be(new CodeAttempt(CodeAttemptOutcome.Taken, 1));
    }
}
