using Comven.Domain.Entities;
using Comven.Domain.Enums;

namespace Comven.Tests;

public sealed class CustomerAccountTests
{
    [Fact]
    public void Debit_ShouldReduceBalance_ForPrepaidAccounts()
    {
        var account = new CustomerAccount(
            Guid.NewGuid(),
            "cli-pre-001",
            "Ada Lovelace",
            AccountType.Prepaid,
            100m,
            new DateOnly(2026, 10, 5));

        account.Debit(35m);

        Assert.Equal(65m, account.AvailableBalance);
    }

    [Fact]
    public void Debit_ShouldThrow_WhenPrepaidBalanceIsInsufficient()
    {
        var account = new CustomerAccount(
            Guid.NewGuid(),
            "cli-pre-001",
            "Ada Lovelace",
            AccountType.Prepaid,
            50m,
            new DateOnly(2026, 10, 5));

        var action = () => account.Debit(75m);

        Assert.Throws<InvalidOperationException>(action);
    }

    [Fact]
    public void Debit_ShouldNotReduceBalance_ForPostpaidAccounts()
    {
        var account = new CustomerAccount(
            Guid.NewGuid(),
            "cli-post-001",
            "Grace Hopper",
            AccountType.Postpaid,
            0m,
            new DateOnly(2026, 10, 15));

        account.Debit(250m);

        Assert.Equal(0m, account.AvailableBalance);
    }
}