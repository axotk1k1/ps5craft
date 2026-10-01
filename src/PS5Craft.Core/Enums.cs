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
    Library,
    Info,
    Tools,
    Settings,
    About
}

public enum LibraryTransferState
{
    NotTransferred,
    Pending,
    InProgress,
    Completed,
    Failed,
    Incomplete
}

public enum TransferDestinationKind
{
    Usb,
    Console
}

public enum DiscoveredDeviceKind
{
    Unknown,
    CompatibleFtp,
    Ps5Craft
}
