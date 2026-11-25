using System;
using System.Collections.Generic;
using System.Linq;

namespace ProbablyCoop.Api;

/// <summary>Game-local message names for the version 1 cooperative extension API.</summary>
public static class CoopExtensionApi
{
    public const string DiscoveryChannel = "probably_coop:extension-discover-v1";
    public const string RegistrationChannel = "extension-register-v1";
    public const string CustomerAppearanceChannel = "probably_coop:customer-appearance-v1";
    /// <summary>Optional game-local Func&lt;GameItem, bool&gt; service for an owned mod item's Activate action.</summary>
    public const string LocalItemActivationService = "probably-coop-local-item-activation-v1";
    public const int MaximumActionBytes = 1024;
    public const int MaximumStateBytes = 4096;
}

/// <summary>A guest-local display event. It does not submit an action or alter the host.</summary>
public sealed class CoopCustomerAppearance
{
    public string PortraitKey { get; }
    public bool Restoring { get; }

    internal CoopCustomerAppearance(string portraitKey, bool restoring)
    {
        PortraitKey = portraitKey;
        Restoring = restoring;
    }
}

public enum CoopExtensionActor { Host, Partner }
public enum CoopActionResult { Applied, Rejected, Stale, Busy, Failed, Disconnected }
public enum CoopActionSubmission { Unavailable, Invalid, Queued, Completed, Busy }

public sealed class CoopExtensionState
{
    public ulong Revision { get; }
    public string Payload { get; }

    internal CoopExtensionState(ulong revision,string payload)
    {
        Revision = revision;
        Payload = payload;
    }
}

public sealed class CoopExtensionAction
{
    public CoopExtensionActor Actor { get; }
    public string Id { get; }
    public string Payload { get; }

    internal CoopExtensionAction(CoopExtensionActor actor,string id,string payload)
    {
        Actor = actor;
        Id = id;
        Payload = payload;
    }
}

/// <summary>Prepare a registration when ProbablyCoop sends the discovery message.</summary>
public sealed class CoopExtensionRegistration
{
    public string Protocol { get; }
    internal string[] Actions { get; }
    internal Func<string> ReadState { get; }
    internal Func<CoopExtensionAction,CoopActionResult> Execute { get; }
    internal Action<CoopExtensionState> ProjectState { get; }
    internal Action<CoopExtensionSession> Attach { get; }

    public CoopExtensionRegistration(string protocol,IEnumerable<string> actions,Func<string> readState,
        Func<CoopExtensionAction,CoopActionResult> execute,Action<CoopExtensionState> projectState,
        Action<CoopExtensionSession> attach)
    {
        Protocol = protocol;
        Actions = (actions ?? throw new ArgumentNullException(nameof(actions))).Take(33).ToArray();
        ReadState = readState ?? throw new ArgumentNullException(nameof(readState));
        Execute = execute ?? throw new ArgumentNullException(nameof(execute));
        ProjectState = projectState ?? throw new ArgumentNullException(nameof(projectState));
        Attach = attach ?? throw new ArgumentNullException(nameof(attach));
    }
}

/// <summary>The current shop's capability. An instance expires when this shop closes.</summary>
public sealed class CoopExtensionSession
{
    private Func<bool> ready;
    private Func<CoopExtensionState> state;
    private Func<string,string,ulong,Action<CoopActionResult>,CoopActionSubmission> request;
    private readonly int gameThread = Environment.CurrentManagedThreadId;

    public CoopExtensionActor LocalActor { get; }
    /// <summary>Whether this handle has permanently expired. Inspect it on the game thread.</summary>
    public bool IsClosed => ready == null;
    public bool IsReady => Environment.CurrentManagedThreadId == gameThread && ready?.Invoke() == true;
    public CoopExtensionState CurrentState => IsReady ? state?.Invoke() : null;

    internal CoopExtensionSession(CoopExtensionActor localActor,Func<bool> ready,Func<CoopExtensionState> state,
        Func<string,string,ulong,Action<CoopActionResult>,CoopActionSubmission> request)
    {
        LocalActor = localActor;
        this.ready = ready;
        this.state = state;
        this.request = request;
    }

    /// <summary>Use the revision of the state displayed when the action was selected.</summary>
    public CoopActionSubmission Request(string actionId,string payload,ulong expectedRevision,Action<CoopActionResult> completed = null)
        => IsReady && request != null ? request(actionId,payload,expectedRevision,completed) : CoopActionSubmission.Unavailable;

    internal void Close()
    {
        ready = null;
        state = null;
        request = null;
    }
}
