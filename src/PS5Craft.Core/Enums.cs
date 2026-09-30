namespace PS5Craft.Core;

public enum OperationState
{
    Idle,
    Preparing,
    Running,
    Completed,
    Failed,
    Cancelled
}

public enum OutputFormat
{
    /// <summary>Default MkPFS pack folder → exFAT-wrapped PFSC (.ffpfsc).</summary>
    Ffpfsc,

    /// <summary>MkPFS pack folder --raw → direct PFS (.ffpfs). Compatibility warnings apply.</summary>
    Ffpfs
}

public enum ProcessPriorityChoice
{
    Normal,
    BelowNormal,
    Low
}

public enum LogLevel
{
    Info,
    Warning,
    Error,
    Success
}

public enum AppPage
{
    Extract,
    Pack,
    Info,
    Tools,
    Settings,
    About
}
