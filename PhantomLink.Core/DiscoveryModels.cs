using System;
using System.Collections.Generic;

namespace PhantomLink.Core
{
    public enum DiscoveryCandidateKind
    {
        Numeric = 0,
        Boolean = 1,
        CollectionCount = 2,
        Enum = 3
    }

    public enum DiscoveryConcept
    {
        HealthCurrent = 0,
        HealthMax = 1,
        AmmoCurrent = 2,
        AmmoMax = 3,
        StaminaCurrent = 4,
        StaminaMax = 5,
        Currency = 6,
        Cooldown = 7,
        InventoryCount = 8,
        Lives = 9,
        Score = 10,
        AbilityCharges = 11,
        ShieldCurrent = 12,
        ShieldMax = 13
    }

    public sealed class DiscoveryCandidate
    {
        public string Key { get; set; }
        public string Fingerprint { get; set; }
        public DiscoveryCandidateKind Kind { get; set; }
        public string GameObjectName { get; set; }
        public string GameObjectPath { get; set; }
        public string ComponentTypeName { get; set; }
        public string DeclaringTypeName { get; set; }
        public string MemberName { get; set; }
        public string MemberTypeName { get; set; }
        public bool CanWrite { get; set; }
        public bool IsStatic { get; set; }
        public long SourceScanId { get; set; }
        public int SeenInScans { get; set; }
        public int TotalScans { get; set; }
        public double PresenceRate { get; set; }
    }

    public sealed class DiscoveryScan
    {
        public long ScanId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public List<DiscoveryCandidate> Candidates { get; set; } = new List<DiscoveryCandidate>();
        public Dictionary<string, DiscoveryCandidate> ByKey { get; set; } = new Dictionary<string, DiscoveryCandidate>(StringComparer.Ordinal);
        public Dictionary<string, DiscoveryCandidate> ByFingerprint { get; set; } = new Dictionary<string, DiscoveryCandidate>(StringComparer.Ordinal);
    }

    public sealed class DiscoverySnapshot
    {
        public long ScanId { get; set; }
        public long Ticks { get; set; }
        public Dictionary<string, string> Values { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class DiscoveryExperimentBeginResult
    {
        public long ExperimentId { get; set; }
        public long ScanId { get; set; }
        public string Label { get; set; }
        public long Ticks { get; set; }
        public Dictionary<string, string> BeforeValues { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class DiscoveryExperimentDelta
    {
        public string Key { get; set; }
        public DiscoveryCandidateKind Kind { get; set; }
        public string Before { get; set; }
        public string After { get; set; }
        public double? Delta { get; set; }
    }

    public sealed class DiscoveryExperimentEndResult
    {
        public long ExperimentId { get; set; }
        public long ScanId { get; set; }
        public string Label { get; set; }
        public long Ticks { get; set; }
        public int Changed { get; set; }
        public int Unchanged { get; set; }
        public List<DiscoveryExperimentDelta> Deltas { get; set; } = new List<DiscoveryExperimentDelta>();
    }

    public sealed class DiscoveryCheatInfo
    {
        public long CheatId { get; set; }
        public int Mode { get; set; }
        public DiscoveryCandidateKind Kind { get; set; }
        public string Key { get; set; }
        public string Fingerprint { get; set; }
    }

    public sealed class DiscoveryCheatListResult
    {
        public List<DiscoveryCheatInfo> Cheats { get; set; } = new List<DiscoveryCheatInfo>();
    }

    public sealed class DiscoveryCandidateScore
    {
        public DiscoveryCandidate Candidate { get; set; }
        public DiscoveryConcept Concept { get; set; }
        public double Score { get; set; }
        public double NameScore { get; set; }
        public double TypeScore { get; set; }
        public double ContextScore { get; set; }
        public double DynamicScore { get; set; }
        public double DatabaseScore { get; set; }
        public double KnowledgeScore { get; set; }
        public double RelationScore { get; set; }
        public double CausalScore { get; set; }
    }

    public sealed class DiscoveryKnowledgeBase
    {
        public int Version { get; set; } = 4;
        public string GameDirectory { get; set; }
        public string GameId { get; set; }
        public string GameGenre { get; set; }
        public string GameVersionLabel { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        public int CheatDefinitionsRevision { get; set; }
        public List<DiscoveryKnowledgeMapping> ConfirmedMappings { get; set; } = new List<DiscoveryKnowledgeMapping>();
        public List<DiscoveryKnowledgeCheat> Cheats { get; set; } = new List<DiscoveryKnowledgeCheat>();
        public List<DiscoveryKnowledgeCheatSetSnapshot> CheatDefinitionHistory { get; set; } = new List<DiscoveryKnowledgeCheatSetSnapshot>();
        public List<DiscoveryKnowledgeSavedEdit> SavedEdits { get; set; } = new List<DiscoveryKnowledgeSavedEdit>();
        public List<DiscoveryKnowledgeExperimentLog> Experiments { get; set; } = new List<DiscoveryKnowledgeExperimentLog>();
        public List<DiscoveryKnowledgeCausalPrior> CausalPriors { get; set; } = new List<DiscoveryKnowledgeCausalPrior>();
        public List<DiscoveryKnowledgeTokenWeight> TokenWeights { get; set; } = new List<DiscoveryKnowledgeTokenWeight>();
        public List<DiscoveryKnowledgeUserFeedback> Feedback { get; set; } = new List<DiscoveryKnowledgeUserFeedback>();
        public List<DiscoveryKnowledgeWriteProbe> WriteProbes { get; set; } = new List<DiscoveryKnowledgeWriteProbe>();
        public List<DiscoveryBehaviorSignature> BehaviorSignatures { get; set; } = new List<DiscoveryBehaviorSignature>();
        public List<DiscoveryRelationEdge> Relations { get; set; } = new List<DiscoveryRelationEdge>();
    }

    public sealed class DiscoveryKnowledgeMapping
    {
        public DiscoveryConcept Concept { get; set; }
        public string Fingerprint { get; set; }
        public string ComponentTypeName { get; set; }
        public string DeclaringTypeName { get; set; }
        public string MemberName { get; set; }
        public string MemberTypeName { get; set; }
        public string GameObjectPathHint { get; set; }
        public double Confidence { get; set; }
        public DateTime ConfirmedUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class DiscoveryKnowledgeCheat
    {
        public string Concept { get; set; }
        public string Fingerprint { get; set; }
        public string Mode { get; set; }
        public string MaxFingerprint { get; set; }
        public double? Threshold { get; set; }
        public double? Value { get; set; }
        public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class DiscoveryKnowledgeCheatSetSnapshot
    {
        public int Revision { get; set; }
        public string Signature { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
        public List<DiscoveryKnowledgeCheat> Cheats { get; set; } = new List<DiscoveryKnowledgeCheat>();
    }

    public sealed class DiscoveryKnowledgeSavedEdit
    {
        public string Command { get; set; }
        public string DisplayName { get; set; }
        public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastAppliedUtc { get; set; }
        public int AppliedCount { get; set; }
    }

    public sealed class DiscoveryKnowledgeExperimentLog
    {
        public string Label { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime EndedUtc { get; set; }
        public List<DiscoveryKnowledgeExperimentDelta> Deltas { get; set; } = new List<DiscoveryKnowledgeExperimentDelta>();
    }

    public sealed class DiscoveryKnowledgeExperimentDelta
    {
        public string Fingerprint { get; set; }
        public string KeyHint { get; set; }
        public string Before { get; set; }
        public string After { get; set; }
        public double? Delta { get; set; }
    }

    public sealed class DiscoveryKnowledgeCausalPrior
    {
        public DiscoveryConcept Concept { get; set; }
        public string EventName { get; set; }
        public string Fingerprint { get; set; }
        public double Strength { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class DiscoveryKnowledgeTokenWeight
    {
        public DiscoveryConcept Concept { get; set; }
        public string Token { get; set; }
        public double Weight { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class DiscoveryKnowledgeUserFeedback
    {
        public string Kind { get; set; }
        public DiscoveryConcept Concept { get; set; }
        public string Fingerprint { get; set; }
        public double Strength { get; set; }
        public string Note { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class DiscoveryKnowledgeWriteProbe
    {
        public DiscoveryConcept Concept { get; set; }
        public string Fingerprint { get; set; }
        public int DelayMs { get; set; }
        public bool Wrote { get; set; }
        public bool Sticky { get; set; }
        public bool RubberBand { get; set; }
        public bool ClampDetected { get; set; }
        public double Score { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class DiscoveryWriteProbeResult
    {
        public long ScanId { get; set; }
        public string Key { get; set; }
        public string Fingerprint { get; set; }
        public DiscoveryCandidateKind Kind { get; set; }
        public int DelayMs { get; set; }
        public bool Wrote { get; set; }
        public bool Reverted { get; set; }
        public bool Sticky { get; set; }
        public bool RubberBand { get; set; }
        public bool ClampDetected { get; set; }
        public double Score { get; set; }
        public string Before { get; set; }
        public string After0 { get; set; }
        public string After { get; set; }
        public string AfterRevert { get; set; }
        public string Probe { get; set; }
    }

    public sealed class DiscoveryCheatRecommendation
    {
        public DiscoveryConcept Concept { get; set; }
        public string Category { get; set; }
        public DiscoveryCandidate Candidate { get; set; }
        public string Mode { get; set; }
        public IReadOnlyDictionary<string, string> Args { get; set; }
        public double Confidence { get; set; }
        public string Reason { get; set; }
    }

    public sealed class DiscoveryObservationSample
    {
        public long Seq { get; set; }
        public long Ticks { get; set; }
        public Dictionary<string, string> Values { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class DiscoveryObservationPullResult
    {
        public long SessionId { get; set; }
        public long Ticks { get; set; }
        public long LastSeq { get; set; }
        public List<string> Events { get; set; } = new List<string>();
        public List<DiscoveryObservationSample> Samples { get; set; } = new List<DiscoveryObservationSample>();
    }

    public sealed class DiscoveryScanStatus
    {
        public long ScanId { get; set; }
        public bool Running { get; set; }
        public bool Done { get; set; }
        public double Progress { get; set; }
        public int Processed { get; set; }
        public int Total { get; set; }
        public int Candidates { get; set; }
        public string Stage { get; set; }
    }

    public sealed class DiscoveryObservationStatus
    {
        public long SessionId { get; set; }
        public string Mode { get; set; }
        public bool Running { get; set; }
        public bool Done { get; set; }
        public long Samples { get; set; }
        public long MaxSamples { get; set; }
        public double Progress { get; set; }
        public int Keys { get; set; }
    }

    public sealed class DiscoveryObservationSummaryEntry
    {
        public string Key { get; set; }
        public long N { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public double Mean { get; set; }
        public double Variance { get; set; }
        public double UpdateRate { get; set; }
        public double IntegerRate { get; set; }
    }

    public sealed class DiscoveryObservationSummaryResult
    {
        public long SessionId { get; set; }
        public long Ticks { get; set; }
        public long Samples { get; set; }
        public int Keys { get; set; }
        public bool Done { get; set; }
        public List<DiscoveryObservationSummaryEntry> Entries { get; set; } = new List<DiscoveryObservationSummaryEntry>();
    }

    public sealed class DiscoveryBehaviorSignature
    {
        public string Fingerprint { get; set; }
        public DiscoveryCandidateKind Kind { get; set; }
        public double? Min { get; set; }
        public double? Max { get; set; }
        public double? Mean { get; set; }
        public double? Variance { get; set; }
        public double UpdateRate { get; set; }
        public bool MostlyInteger { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class DiscoveryRelationEdge
    {
        public string A_Fingerprint { get; set; }
        public string B_Fingerprint { get; set; }
        public string Kind { get; set; }
        public double Strength { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class DiscoveryExperimentSuggestion
    {
        public DiscoveryConcept Concept { get; set; }
        public string Label { get; set; }
        public string EventName { get; set; }
        public int ObserveSeconds { get; set; }
        public string Prompt { get; set; }
    }
}
