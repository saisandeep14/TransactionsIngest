using Xunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TransactionsIngest.Models;

namespace TransactionsIngest.Tests;

public sealed class IngestionServiceTests
{
    [Fact]
    public async Task NewTransaction_IsInserted_WithAuditRow()
    {
        using var db = TestFactory.CreateDb();
        var dto = TestFactory.MakeDto("T-100", amount: 29.99m);
        var svc = TestFactory.CreateService(db, [dto]);

        await svc.RunAsync();

        var record = await db.Transactions.FindAsync("T-100");
        record.Should().NotBeNull();
        record!.Amount.Should().Be(29.99m);
        record.Status.Should().Be(TransactionStatus.Active);
        record.CardLast4.Should().Be("1111");

        var audit = await db.Audits.SingleAsync(a => a.TransactionId == "T-100");
        audit.ChangeType.Should().Be(ChangeTypes.Insert);
    }

    [Fact]
    public async Task ChangedAmount_IsDetected_AndAudited()
    {
        using var db = TestFactory.CreateDb();

        await TestFactory.CreateService(db, [TestFactory.MakeDto("T-200", amount: 10.00m)])
                         .RunAsync();

        await TestFactory.CreateService(db, [TestFactory.MakeDto("T-200", amount: 15.50m)])
                         .RunAsync();

        var record = await db.Transactions.FindAsync("T-200");
        record!.Amount.Should().Be(15.50m);

        var audits = await db.Audits
            .Where(a => a.TransactionId == "T-200")
            .OrderBy(a => a.Id)
            .ToListAsync();

        audits.Should().HaveCount(2);
        audits[0].ChangeType.Should().Be(ChangeTypes.Insert);
        audits[1].ChangeType.Should().Be(ChangeTypes.Update);
        audits[1].ChangeDetail.Should().Contain("Amount: 10.00 -> 15.50");
    }

    [Fact]
    public async Task ChangedLocation_IsDetected_AndAudited()
    {
        using var db = TestFactory.CreateDb();

        await TestFactory.CreateService(db, [TestFactory.MakeDto("T-201", location: "STO-01")])
                         .RunAsync();
        await TestFactory.CreateService(db, [TestFactory.MakeDto("T-201", location: "STO-99")])
                         .RunAsync();

        var audit = await db.Audits
            .Where(a => a.TransactionId == "T-201" && a.ChangeType == ChangeTypes.Update)
            .SingleAsync();

        audit.ChangeDetail.Should().Contain("STO-01 -> STO-99");
    }

    [Fact]
    public async Task IdenticalRun_ProducesNoNewAuditRows()
    {
        using var db = TestFactory.CreateDb();
        var dto = TestFactory.MakeDto("T-300");

        await TestFactory.CreateService(db, [dto]).RunAsync();
        int countAfterFirst = await db.Audits.CountAsync();

        await TestFactory.CreateService(db, [dto]).RunAsync();
        int countAfterSecond = await db.Audits.CountAsync();

        countAfterSecond.Should().Be(countAfterFirst);
    }

    [Fact]
    public async Task IdenticalRun_ProducesNoDuplicateTransactionRows()
    {
        using var db = TestFactory.CreateDb();
        var dto = TestFactory.MakeDto("T-301");

        await TestFactory.CreateService(db, [dto]).RunAsync();
        await TestFactory.CreateService(db, [dto]).RunAsync();

        var count = await db.Transactions.CountAsync(t => t.TransactionId == "T-301");
        count.Should().Be(1);
    }

    [Fact]
    public async Task ActiveRecord_AbsentInNextSnapshot_IsRevoked()
    {
        using var db = TestFactory.CreateDb();

        await TestFactory.CreateService(db, [TestFactory.MakeDto("T-400")])
                         .RunAsync();

        // T-400 is missing from this snapshot
        await TestFactory.CreateService(db, [TestFactory.MakeDto("T-999")])
                         .RunAsync();

        var record = await db.Transactions.FindAsync("T-400");
        record!.Status.Should().Be(TransactionStatus.Revoked);

        var revokeAudit = await db.Audits
            .Where(a => a.TransactionId == "T-400" && a.ChangeType == ChangeTypes.Revoke)
            .SingleAsync();

        revokeAudit.Should().NotBeNull();
    }

    [Fact]
    public async Task RevokedRecord_ReappearsInSnapshot_IsReActivated()
    {
        using var db = TestFactory.CreateDb();

        await TestFactory.CreateService(db, [TestFactory.MakeDto("T-401")]).RunAsync();
        await TestFactory.CreateService(db, []).RunAsync();
        await TestFactory.CreateService(db, [TestFactory.MakeDto("T-401")]).RunAsync();

        var record = await db.Transactions.FindAsync("T-401");
        record!.Status.Should().Be(TransactionStatus.Active);
    }

    [Fact]
    public async Task ActiveRecord_OlderThanWindow_IsFinalized()
    {
        using var db = TestFactory.CreateDb();

        var oldDto = TestFactory.MakeDto("T-500", timestamp: DateTime.UtcNow.AddHours(-30));
        await TestFactory.CreateService(db, [oldDto]).RunAsync();
        await TestFactory.CreateService(db, []).RunAsync();

        var record = await db.Transactions.FindAsync("T-500");
        record!.Status.Should().Be(TransactionStatus.Finalized);

        await db.Audits
            .Where(a => a.TransactionId == "T-500" && a.ChangeType == ChangeTypes.Finalize)
            .SingleAsync();
    }

    [Fact]
    public async Task FinalizedRecord_IsNotModifiedBySubsequentRuns()
    {
        using var db = TestFactory.CreateDb();

        var oldDto = TestFactory.MakeDto("T-501", amount: 5.00m, timestamp: DateTime.UtcNow.AddHours(-30));

        await TestFactory.CreateService(db, [oldDto]).RunAsync();
        await TestFactory.CreateService(db, []).RunAsync();

        int auditCountAfterFinalize = await db.Audits.CountAsync(a => a.TransactionId == "T-501");

        var updatedDto = TestFactory.MakeDto("T-501", amount: 99.99m, timestamp: DateTime.UtcNow.AddHours(-30));
        await TestFactory.CreateService(db, [updatedDto]).RunAsync();

        var record = await db.Transactions.FindAsync("T-501");
        record!.Amount.Should().Be(5.00m);
        record.Status.Should().Be(TransactionStatus.Finalized);

        int auditCountAfterThirdRun = await db.Audits.CountAsync(a => a.TransactionId == "T-501");
        auditCountAfterThirdRun.Should().Be(auditCountAfterFinalize);
    }

    [Fact]
    public async Task MultipleChangedFields_AllCapturedInSingleAuditRow()
    {
        using var db = TestFactory.CreateDb();

        await TestFactory.CreateService(db, [
            TestFactory.MakeDto("T-600", amount: 10m, product: "Widget", location: "STO-01")
        ]).RunAsync();

        await TestFactory.CreateService(db, [
            TestFactory.MakeDto("T-600", amount: 20m, product: "Gadget", location: "STO-02")
        ]).RunAsync();

        var updateAudit = await db.Audits
            .Where(a => a.TransactionId == "T-600" && a.ChangeType == ChangeTypes.Update)
            .SingleAsync();

        updateAudit.ChangeDetail.Should().Contain("Amount");
        updateAudit.ChangeDetail.Should().Contain("ProductName");
        updateAudit.ChangeDetail.Should().Contain("LocationCode");
    }

    [Fact]
    public async Task FullCardNumber_IsNeverPersisted_OnlyLast4Stored()
    {
        using var db = TestFactory.CreateDb();
        var dto = TestFactory.MakeDto("T-700", card: "4111222233334444");
        await TestFactory.CreateService(db, [dto]).RunAsync();

        var record = await db.Transactions.FindAsync("T-700");
        record!.CardLast4.Should().Be("4444");
        record.CardLast4.Should().NotContain("4111222233334444");
    }
}