namespace CodexBackdrop;

public enum ShutdownContinuationDecision
{
    Refuse,
    UseFreshSelection,
    UseRecordedSelection,
}

public static class CodexShutdownContinuationPolicy
{
    public static ShutdownContinuationDecision Decide(
        bool rootIdentityStillExact,
        bool inventoryFullyVerifiable,
        bool freshSelectionAccepted)
    {
        if (!rootIdentityStillExact) return ShutdownContinuationDecision.Refuse;
        return inventoryFullyVerifiable && freshSelectionAccepted
            ? ShutdownContinuationDecision.UseFreshSelection
            : ShutdownContinuationDecision.UseRecordedSelection;
    }
}
