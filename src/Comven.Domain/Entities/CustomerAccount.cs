using Comven.Domain.Enums;

namespace Comven.Domain.Entities;

public sealed class CustomerAccount
{
    public CustomerAccount(
        Guid id,
        string externalId,
        string name,
        AccountType accountType,
        decimal availableBalance,
        DateOnly billingDay)
    {
        if (string.IsNullOrWhiteSpace(externalId))
        {
            throw new ArgumentException("External id is required.", nameof(externalId));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        if (availableBalance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableBalance), "Balance cannot be negative.");
        }

        Id = id;
        ExternalId = externalId;
        Name = name;
        AccountType = accountType;
        AvailableBalance = availableBalance;
        BillingDay = billingDay;
    }

    public Guid Id { get; }

    public string ExternalId { get; }

    public string Name { get; }

    public AccountType AccountType { get; }

    public decimal AvailableBalance { get; private set; }

    public DateOnly BillingDay { get; }

    public bool CanDebit(decimal amount)
    {
        ValidateAmount(amount);
        return AccountType == AccountType.Postpaid || AvailableBalance >= amount;
    }

    public void Debit(decimal amount)
    {
        ValidateAmount(amount);

        if (!CanDebit(amount))
        {
            throw new InvalidOperationException("Insufficient balance for prepaid account.");
        }

        if (AccountType == AccountType.Prepaid)
        {
            AvailableBalance -= amount;
        }
    }

    public void Credit(decimal amount)
    {
        ValidateAmount(amount);
        AvailableBalance += amount;
    }

    private static void ValidateAmount(decimal amount)
    {
        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be greater than zero.");
        }
    }
}