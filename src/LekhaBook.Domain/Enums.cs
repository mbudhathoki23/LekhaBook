namespace LekhaBook.Domain;

/// <summary>
/// Every party, money account and income/expense head is an <see cref="Account"/>.
/// See AGENT_PLAN § 5.1 and DECISIONS D7.
/// </summary>
public enum AccountType
{
    /// <summary>Customer or supplier.</summary>
    Party,

    /// <summary>Cash in hand or petty cash.</summary>
    Cash,

    Bank,

    /// <summary>Cheques received but not yet deposited (a holding account).</summary>
    Cheque,

    /// <summary>Online wallet (eSewa, Khalti, …).</summary>
    Wallet,

    /// <summary>Income head, for example Salary or Commission.</summary>
    Income,

    /// <summary>Expense head, for example Fuel or Food.</summary>
    Expense,

    /// <summary>Internal balancing account (Adjustment). Not shown in pickers.</summary>
    System,
}

public enum PartyType
{
    Customer,
    Supplier,
}

/// <summary>
/// Side of an opening balance. For a party, <see cref="Debit"/> is "To Receive" and
/// <see cref="Credit"/> is "To Give". For a money account, <see cref="Debit"/> means money held.
/// </summary>
public enum BalanceSide
{
    Debit,
    Credit,
}

/// <summary>Transaction types and their debit/credit rules are in <see cref="PostingRules"/>.</summary>
public enum TransactionType
{
    PaymentIn,
    PaymentOut,
    PartyAdjustAdd,
    PartyAdjustReduce,
    Expense,
    OtherIncome,
    AddMoney,
    ReduceMoney,
    TransferMoney,
}

public enum TransactionStatus
{
    Posted,
    Void,
}

public enum CalendarMode
{
    /// <summary>Bikram Sambat (Nepali calendar), the default.</summary>
    BS,
    AD,
}
