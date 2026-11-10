namespace MikrotikWorker.MikroTik;

public sealed record InstalledPackageInfo(
    string Name,
    string Version,
    bool Disabled);

public sealed record RouterFileInfo(
    string Name,
    long SizeBytes);
