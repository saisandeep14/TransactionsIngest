namespace TransactionsIngest.Models;

public enum TransactionStatus
{
    /// <summary>Transaction is active and may be updated by future runs.</summary>
    Active = 0,

    /// <summary>
    /// Transaction was present in a previous snapshot but is absent in the
    /// current one while still within the 24-hour window. Treated as cancelled.
    /// </summary>
    Revoked = 1,

    /// <summary>
    /// Transaction is older than the snapshot window and will not change.
    /// </summary>
    Finalized = 2
}
