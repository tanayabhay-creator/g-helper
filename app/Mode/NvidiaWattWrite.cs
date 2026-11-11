namespace GHelper.Mode;

// A driver rejection is not a lock. The refusal follows the existing RC71L guard:
// XgMitigation is enabled only for an Ally model. Other models are not blocked here.
public enum NvidiaWattTrigger
{
    Activation,
    Startup,
    ProfileChange,
    SavedReplay,
    ExplicitClick
}

public static class NvidiaWattWrite
{
    public static bool MayAttempt(NvidiaWattTrigger trigger, XgDockSnapshot snap, bool admissionClosed)
        => MayAttempt(trigger, snap, admissionClosed, XgMitigation.Shared.Enabled);

    public static bool MayAttempt(NvidiaWattTrigger trigger, XgDockSnapshot snap, bool admissionClosed, bool guarded)
    {
        _ = trigger;
        _ = snap;
        _ = admissionClosed;
        return !guarded;
    }
}
