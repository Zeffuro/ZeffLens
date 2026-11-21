using System;
using System.Runtime.Loader;

using ZeffLens.Diagnostics;

namespace ZeffLens;

public sealed partial class MemoryDiagnosticsService
{
    private sealed record PluginContextObservations(
        int AssemblyLoadContextCount,
        PluginContextObservation[] PluginContexts);

    private sealed record PluginContextObservation(
        string InternalName,
        string Name,
        bool IsLoaded,
        bool IsDev,
        bool IsTesting,
        bool IsThirdParty,
        string? LoadContextName,
        int ContextId,
        int AssemblyCount,
        string[] AssemblyNames,
        AssemblyLoadContext Context);

    private sealed class TrackedPluginContext(string internalName, int generation)
    {
        public string InternalName { get; } = internalName;

        public string Name { get; set; } = internalName;

        public int Generation { get; } = generation;

        public bool IsLoaded { get; set; }

        public bool IsDev { get; set; }

        public bool IsTesting { get; set; }

        public bool IsThirdParty { get; set; }

        public string? LoadContextName { get; set; }

        public int ContextId { get; set; }

        public int AssemblyCount { get; set; }

        public string[] AssemblyNames { get; set; } = Array.Empty<string>();

        public DateTime LastSeenUtc { get; set; }

        public DateTime? LastUnloadUtc { get; set; }

        public WeakReference<AssemblyLoadContext>? ContextReference { get; set; }

        public bool SuspectedRetainedAfterUnload { get; set; }

        public bool AssemblyLoadContextAlive => ContextReference?.TryGetTarget(out _) == true;

        public void UpdateFrom(PluginContextObservation observation, DateTime timestamp)
        {
            Name = observation.Name;
            IsLoaded = observation.IsLoaded;
            IsDev = observation.IsDev;
            IsTesting = observation.IsTesting;
            IsThirdParty = observation.IsThirdParty;
            LoadContextName = observation.LoadContextName;
            ContextId = observation.ContextId;
            LastSeenUtc = timestamp;
            ContextReference = new WeakReference<AssemblyLoadContext>(observation.Context);
            SuspectedRetainedAfterUnload = false;

            if (observation.AssemblyNames.Length > 0)
            {
                AssemblyNames = observation.AssemblyNames;
                AssemblyCount = observation.AssemblyNames.Length;
            }
            else if (observation.AssemblyCount > 0)
            {
                AssemblyCount = observation.AssemblyCount;
            }

            if (observation.IsLoaded)
            {
                LastUnloadUtc = null;
            }
        }

        public bool IsSameContext(AssemblyLoadContext context)
            => ContextReference?.TryGetTarget(out var trackedContext) == true &&
               ReferenceEquals(trackedContext, context);

        public PluginContextSnapshot ToSnapshot()
        {
            return new PluginContextSnapshot(
                InternalName,
                Name,
                Generation,
                IsLoaded,
                IsRetired: false,
                IsDev,
                IsTesting,
                IsThirdParty,
                LoadContextName,
                ContextId,
                AssemblyCount,
                AssemblyNames,
                LastSeenUtc,
                LastUnloadUtc,
                RetiredUtc: null,
                RetiredReason: null,
                ForcedGcChecks: 0,
                AssemblyLoadContextAlive,
                SuspectedRetainedAfterUnload);
        }

        public ProbeTarget? ToProbeTarget()
        {
            if (ContextReference?.TryGetTarget(out var context) != true)
            {
                return null;
            }

            var owner = new ProbeOwner(
                ProbeOwner.BuildKey(InternalName, ContextId, Generation),
                InternalName,
                Name,
                Generation,
                IsLoaded,
                IsRetiredContext: false,
                LoadContextName,
                ContextId);

            return new ProbeTarget(owner, context!);
        }
    }

    private sealed class RetiredPluginContext
    {
        private RetiredPluginContext(
            string internalName,
            string name,
            int generation,
            bool isDev,
            bool isTesting,
            bool isThirdParty,
            string? loadContextName,
            int contextId,
            int assemblyCount,
            string[] assemblyNames,
            DateTime lastSeenUtc,
            DateTime retiredUtc,
            string retiredReason,
            WeakReference<AssemblyLoadContext> contextReference)
        {
            InternalName = internalName;
            Name = name;
            Generation = generation;
            IsDev = isDev;
            IsTesting = isTesting;
            IsThirdParty = isThirdParty;
            LoadContextName = loadContextName;
            ContextId = contextId;
            AssemblyCount = assemblyCount;
            AssemblyNames = assemblyNames;
            LastSeenUtc = lastSeenUtc;
            RetiredUtc = retiredUtc;
            RetiredReason = retiredReason;
            ContextReference = contextReference;
        }

        public string InternalName { get; }

        public string Name { get; }

        public int Generation { get; }

        public bool IsDev { get; }

        public bool IsTesting { get; }

        public bool IsThirdParty { get; }

        public string? LoadContextName { get; }

        public int ContextId { get; }

        public int AssemblyCount { get; }

        public string[] AssemblyNames { get; }

        public DateTime LastSeenUtc { get; }

        public DateTime RetiredUtc { get; }

        public string RetiredReason { get; }

        public WeakReference<AssemblyLoadContext> ContextReference { get; }

        public int ForcedGcChecks { get; private set; }

        public DateTime? LastRetentionCheckUtc { get; private set; }

        public bool SuspectedRetainedAfterUnload { get; private set; }

        public bool AssemblyLoadContextAlive => ContextReference.TryGetTarget(out _);

        public static RetiredPluginContext FromTracked(TrackedPluginContext context, DateTime retiredUtc, string retiredReason)
        {
            var contextReference = context.ContextReference ??
                                   throw new InvalidOperationException("Tracked plugin context did not have an AssemblyLoadContext reference.");
            return new RetiredPluginContext(
                context.InternalName,
                context.Name,
                context.Generation,
                context.IsDev,
                context.IsTesting,
                context.IsThirdParty,
                context.LoadContextName,
                context.ContextId,
                context.AssemblyCount,
                context.AssemblyNames,
                context.LastSeenUtc,
                retiredUtc,
                retiredReason,
                contextReference);
        }

        public void UpdateRetentionMarkers(bool forcedGcJustRan, DateTime timestamp)
        {
            LastRetentionCheckUtc = timestamp;
            if (forcedGcJustRan)
            {
                ForcedGcChecks++;
            }

            SuspectedRetainedAfterUnload = ForcedGcChecks > 0 && AssemblyLoadContextAlive;
        }

        public PluginContextSnapshot ToSnapshot()
        {
            return new PluginContextSnapshot(
                InternalName,
                Name,
                Generation,
                IsLoaded: false,
                IsRetired: true,
                IsDev,
                IsTesting,
                IsThirdParty,
                LoadContextName,
                ContextId,
                AssemblyCount,
                AssemblyNames,
                LastSeenUtc,
                RetiredUtc,
                RetiredUtc,
                RetiredReason,
                ForcedGcChecks,
                AssemblyLoadContextAlive,
                SuspectedRetainedAfterUnload);
        }

        public ProbeTarget? ToProbeTarget()
        {
            if (!ContextReference.TryGetTarget(out var context))
            {
                return null;
            }

            var owner = new ProbeOwner(
                ProbeOwner.BuildKey(InternalName, ContextId, Generation),
                InternalName,
                Name,
                Generation,
                IsLoaded: false,
                IsRetiredContext: true,
                LoadContextName,
                ContextId);

            return new ProbeTarget(owner, context);
        }
    }
}
