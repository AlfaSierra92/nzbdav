namespace NzbWebDAV.Tests.Services;

/// <summary>
/// Serializes every test class that touches <see cref="NzbWebDAV.Services.Par2RepairThrottle"/>.
/// The throttle is process-wide static state, so a throttle test holding a slot would
/// otherwise make a concurrently-running repair test return NotAttempted.
/// </summary>
[CollectionDefinition("Par2Repair")]
public class Par2RepairCollection;
