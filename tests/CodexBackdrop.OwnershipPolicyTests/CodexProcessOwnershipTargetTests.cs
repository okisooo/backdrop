using CodexBackdrop;

internal static class CodexProcessOwnershipTargetTests
{
    public static void RunAll()
    {
        IncludesOnlyRecordedRootAndVerifiedDescendants();
        AllowsRootAfterItsWindowCloses();
        RejectsMissingOrReusedRecordedRoot();
        RejectsRootIdentityAttestationChanges();
        RejectsDescendantSessionMismatch();
        RejectsDescendantOwnerSidMismatch();
        ExcludesOtherVisibleRootsOnceOwnershipIsRecorded();
        RejectsUnverifiedOwnedDescendants();
        RejectsDuplicateProcessIdentities();
        RejectsProcessIdReuseWithinSnapshot();
        ExcludesUnrelatedAndBrokenHelpers();
        RejectsHelperPidReuse();
        AllowsDescendantWithEqualStartTime();
        ExcludesDescendantsStartedBeforeRecordedRoot();
        ExcludesTemporalInversionsInNestedAncestry();
        RejectsCyclesSafely();
        RejectsHelpersWithoutAGeneration();
        RejectsInvalidGenerationContract();
        RejectsInvalidGenerationRoot();
        RejectsInvalidSnapshotEntries();
    }

    private static void IncludesOnlyRecordedRootAndVerifiedDescendants()
    {
        var root = OwnershipPolicyTestData.Root();
        var generation = new CodexLaunchGeneration(root.Identity, OwnershipPolicyTestData.Package);
        var direct = OwnershipPolicyTestData.Helper(11, root.Identity, 20);
        var nested = OwnershipPolicyTestData.Helper(12, direct.Identity, 21);
        var unrelated = OwnershipPolicyTestData.Helper(13, OwnershipPolicyTestData.Identity(99, 40), 22);

        var result = CodexProcessOwnership.SelectOwnedTargets(generation, [root, direct, nested, unrelated]);

        OwnershipPolicyTestData.Expect(result.Accepted, "verified ownership tree was rejected");
        OwnershipPolicyTestData.Expect(
            result.Targets.Select(process => process.Identity).SequenceEqual([root.Identity, direct.Identity, nested.Identity]),
            "selection included a process outside the recorded root tree");
    }

    private static void AllowsRootAfterItsWindowCloses()
    {
        var root = OwnershipPolicyTestData.Root();
        var generation = new CodexLaunchGeneration(root.Identity, OwnershipPolicyTestData.Package);
        var closedRoot = root with { HasVisibleWindow = false };
        var result = CodexProcessOwnership.SelectOwnedTargets(generation, [closedRoot]);

        OwnershipPolicyTestData.Expect(result.Accepted, "recorded root was rejected after its window closed");
        OwnershipPolicyTestData.Expect(result.Targets.Count == 1 && result.Targets[0].Identity == root.Identity, "closed root was not retained as the only target");
    }

    private static void RejectsMissingOrReusedRecordedRoot()
    {
        var root = OwnershipPolicyTestData.Root();
        var generation = new CodexLaunchGeneration(root.Identity, OwnershipPolicyTestData.Package);
        var missing = CodexProcessOwnership.SelectOwnedTargets(generation, [OwnershipPolicyTestData.Helper(11, root.Identity)]);
        var reused = CodexProcessOwnership.SelectOwnedTargets(generation, [OwnershipPolicyTestData.Root(startSeconds: 11)]);

        OwnershipPolicyTestData.ExpectFailure(CodexOwnershipFailure.RecordedRootMissingOrReused, missing, "missing recorded root");
        OwnershipPolicyTestData.ExpectFailure(CodexOwnershipFailure.RecordedRootMissingOrReused, reused, "reused recorded root pid");
    }

    private static void RejectsRootIdentityAttestationChanges()
    {
        var root = OwnershipPolicyTestData.Root();
        var generation = new CodexLaunchGeneration(root.Identity, OwnershipPolicyTestData.Package);
        var changed = new[]
        {
            root with { PackageIdentity = "other-package" },
            root with { PublisherIdentity = "CN=Other Publisher" },
            root with { ExecutablePath = Path.Combine(Path.GetTempPath(), "other.exe") },
        };

        foreach (var current in changed)
        {
            var result = CodexProcessOwnership.SelectOwnedTargets(generation, [current]);
            OwnershipPolicyTestData.ExpectFailure(CodexOwnershipFailure.RecordedRootMissingOrReused, result, "changed root attestation");
        }
    }

    private static void RejectsDescendantSessionMismatch()
    {
        var root = OwnershipPolicyTestData.Root();
        var generation = new CodexLaunchGeneration(root.Identity, OwnershipPolicyTestData.Package);
        var foreignSession = OwnershipPolicyTestData.Helper(
            11,
            root.Identity,
            sessionId: OwnershipPolicyTestData.SessionId + 1);
        var result = CodexProcessOwnership.SelectOwnedTargets(generation, [root, foreignSession]);

        OwnershipPolicyTestData.ExpectFailure(
            CodexOwnershipFailure.UnverifiableIdentity,
            result,
            "descendant session mismatch");
    }

    private static void RejectsDescendantOwnerSidMismatch()
    {
        var root = OwnershipPolicyTestData.Root();
        var generation = new CodexLaunchGeneration(root.Identity, OwnershipPolicyTestData.Package);
        var foreignOwner = OwnershipPolicyTestData.Helper(
            11,
            root.Identity,
            userSid: "S-1-5-21-100-200-300-1002");
        var result = CodexProcessOwnership.SelectOwnedTargets(generation, [root, foreignOwner]);

        OwnershipPolicyTestData.ExpectFailure(
            CodexOwnershipFailure.UnverifiableIdentity,
            result,
            "descendant owner sid mismatch");
    }

    private static void ExcludesOtherVisibleRootsOnceOwnershipIsRecorded()
    {
        var root = OwnershipPolicyTestData.Root();
        var generation = new CodexLaunchGeneration(root.Identity, OwnershipPolicyTestData.Package);
        var secondRoot = OwnershipPolicyTestData.Root(12, 11);
        var visibleHelper = OwnershipPolicyTestData.Helper(11, root.Identity, visible: true);

        var withSecondRoot = CodexProcessOwnership.SelectOwnedTargets(generation, [root, secondRoot]);
        OwnershipPolicyTestData.Expect(withSecondRoot.Accepted, "a second visible root invalidated an exact recorded generation");
        OwnershipPolicyTestData.Expect(
            withSecondRoot.Targets.Count == 1 && withSecondRoot.Targets[0].Identity == root.Identity,
            "an unrelated visible root became a destructive target");

        var withVisibleHelper = CodexProcessOwnership.SelectOwnedTargets(generation, [root, visibleHelper]);
        OwnershipPolicyTestData.Expect(withVisibleHelper.Accepted, "a verified visible helper invalidated its recorded root");
        OwnershipPolicyTestData.Expect(withVisibleHelper.Targets.Count == 2, "a verified child was lost from the owned tree");
    }

    private static void RejectsUnverifiedOwnedDescendants()
    {
        var root = OwnershipPolicyTestData.Root();
        var generation = new CodexLaunchGeneration(root.Identity, OwnershipPolicyTestData.Package);
        var invalid = new[]
        {
            OwnershipPolicyTestData.Helper(11, root.Identity, package: "other-package"),
            OwnershipPolicyTestData.Helper(12, root.Identity, publisher: "CN=Other Publisher"),
            OwnershipPolicyTestData.Helper(13, root.Identity, executable: Path.Combine(Path.GetTempPath(), "other.exe")),
            OwnershipPolicyTestData.Helper(14, root.Identity, role: CodexProcessRole.Unknown),
        };

        foreach (var helper in invalid)
        {
            var result = CodexProcessOwnership.SelectOwnedTargets(generation, [root, helper]);
            OwnershipPolicyTestData.ExpectFailure(CodexOwnershipFailure.UnverifiableIdentity, result, "unverified owned descendant");
        }
    }

    private static void RejectsDuplicateProcessIdentities()
    {
        var root = OwnershipPolicyTestData.Root();
        var generation = new CodexLaunchGeneration(root.Identity, OwnershipPolicyTestData.Package);
        var duplicate = root with { HasVisibleWindow = false };
        var result = CodexProcessOwnership.SelectOwnedTargets(generation, [root, duplicate]);

        OwnershipPolicyTestData.ExpectFailure(CodexOwnershipFailure.UnverifiableIdentity, result, "duplicate process identity");
    }

    private static void RejectsProcessIdReuseWithinSnapshot()
    {
        var root = OwnershipPolicyTestData.Root();
        var generation = new CodexLaunchGeneration(root.Identity, OwnershipPolicyTestData.Package);
        var reused = OwnershipPolicyTestData.Helper(root.Identity.ProcessId, root.Identity, startSeconds: 20);
        var result = CodexProcessOwnership.SelectOwnedTargets(generation, [root, reused]);

        OwnershipPolicyTestData.ExpectFailure(CodexOwnershipFailure.UnverifiableIdentity, result, "reused process id in snapshot");
    }

    private static void ExcludesUnrelatedAndBrokenHelpers()
    {
        var root = OwnershipPolicyTestData.Root();
        var generation = new CodexLaunchGeneration(root.Identity, OwnershipPolicyTestData.Package);
        var unrelated = OwnershipPolicyTestData.Helper(11, OwnershipPolicyTestData.Identity(99, 40));
        var missingParent = OwnershipPolicyTestData.Helper(12, null!);

        var result = CodexProcessOwnership.SelectOwnedTargets(generation, [root, unrelated, missingParent]);

        OwnershipPolicyTestData.Expect(result.Accepted, "unrelated helper made the ownership selection fail");
        OwnershipPolicyTestData.Expect(result.Targets.Count == 1 && result.Targets[0].Identity == root.Identity, "broken helper became a destructive target");
    }

    private static void RejectsHelperPidReuse()
    {
        var root = OwnershipPolicyTestData.Root();
        var generation = new CodexLaunchGeneration(root.Identity, OwnershipPolicyTestData.Package);
        var reusedParent = OwnershipPolicyTestData.Helper(11, new CodexProcessIdentity(root.Identity.ProcessId, root.Identity.StartTimeUtc.AddSeconds(1)));
        var result = CodexProcessOwnership.SelectOwnedTargets(generation, [root, reusedParent]);

        OwnershipPolicyTestData.Expect(result.Accepted, "helper pid reuse made the safe selection fail");
        OwnershipPolicyTestData.Expect(result.Targets.Count == 1, "helper with reused parent pid became a target");
    }

    private static void ExcludesDescendantsStartedBeforeRecordedRoot()
    {
        var root = OwnershipPolicyTestData.Root();
        var generation = new CodexLaunchGeneration(root.Identity, OwnershipPolicyTestData.Package);
        var staleDescendant = OwnershipPolicyTestData.Helper(11, root.Identity, startSeconds: 9);
        var result = CodexProcessOwnership.SelectOwnedTargets(generation, [root, staleDescendant]);

        OwnershipPolicyTestData.Expect(result.Accepted, "a stale descendant made the safe root selection fail");
        OwnershipPolicyTestData.Expect(
            result.Targets.Count == 1 && result.Targets[0].Identity == root.Identity,
            "a descendant that predates the recorded root became a target");
    }

    private static void AllowsDescendantWithEqualStartTime()
    {
        var root = OwnershipPolicyTestData.Root();
        var generation = new CodexLaunchGeneration(root.Identity, OwnershipPolicyTestData.Package);
        var sameTimestamp = OwnershipPolicyTestData.Helper(11, root.Identity, startSeconds: 10);
        var result = CodexProcessOwnership.SelectOwnedTargets(generation, [root, sameTimestamp]);

        OwnershipPolicyTestData.Expect(result.Accepted, "an equal-start descendant was rejected");
        OwnershipPolicyTestData.Expect(result.Targets.Count == 2, "an equal-start descendant was not retained");
    }

    private static void ExcludesTemporalInversionsInNestedAncestry()
    {
        var root = OwnershipPolicyTestData.Root();
        var generation = new CodexLaunchGeneration(root.Identity, OwnershipPolicyTestData.Package);
        var direct = OwnershipPolicyTestData.Helper(11, root.Identity, startSeconds: 20);
        var staleNested = OwnershipPolicyTestData.Helper(12, direct.Identity, startSeconds: 19);
        var result = CodexProcessOwnership.SelectOwnedTargets(generation, [root, direct, staleNested]);

        OwnershipPolicyTestData.Expect(result.Accepted, "a temporal ancestry inversion made the safe root selection fail");
        OwnershipPolicyTestData.Expect(
            result.Targets.Select(process => process.Identity).SequenceEqual([root.Identity, direct.Identity]),
            "a nested descendant with an impossible start time became a target");
    }

    private static void RejectsCyclesSafely()
    {
        var root = OwnershipPolicyTestData.Root();
        var generation = new CodexLaunchGeneration(root.Identity, OwnershipPolicyTestData.Package);
        var first = OwnershipPolicyTestData.Helper(11, OwnershipPolicyTestData.Identity(12, 21), 20);
        var second = OwnershipPolicyTestData.Helper(12, first.Identity, 21);
        var result = CodexProcessOwnership.SelectOwnedTargets(generation, [root, first, second]);

        OwnershipPolicyTestData.Expect(result.Accepted, "unrelated cycle made the ownership selection fail");
        OwnershipPolicyTestData.Expect(result.Targets.Count == 1, "cyclic helpers became destructive targets");
    }

    private static void RejectsHelpersWithoutAGeneration()
    {
        var helper = OwnershipPolicyTestData.Helper(11, OwnershipPolicyTestData.Identity(10, 10));
        var result = CodexProcessOwnership.SelectOwnedTargets(null, [helper]);

        OwnershipPolicyTestData.ExpectFailure(CodexOwnershipFailure.InvalidPackageContract, result, "helper without generation");
    }

    private static void RejectsInvalidGenerationContract()
    {
        var root = OwnershipPolicyTestData.Root();
        var invalid = new CodexLaunchGeneration(root.Identity, new CodexPackageContract(
            OwnershipPolicyTestData.Package.PackageIdentity,
            OwnershipPolicyTestData.Package.PublisherIdentity,
            "relative\\ChatGPT.exe"));
        var result = CodexProcessOwnership.SelectOwnedTargets(invalid, [root]);

        OwnershipPolicyTestData.ExpectFailure(CodexOwnershipFailure.InvalidPackageContract, result, "invalid generation contract");
    }

    private static void RejectsInvalidGenerationRoot()
    {
        var invalid = new CodexLaunchGeneration(
            new CodexProcessIdentity(10, default),
            OwnershipPolicyTestData.Package);
        var result = CodexProcessOwnership.SelectOwnedTargets(invalid, [OwnershipPolicyTestData.Root()]);

        OwnershipPolicyTestData.ExpectFailure(CodexOwnershipFailure.InvalidPackageContract, result, "invalid generation root");
    }

    private static void RejectsInvalidSnapshotEntries()
    {
        var root = OwnershipPolicyTestData.Root();
        var generation = new CodexLaunchGeneration(root.Identity, OwnershipPolicyTestData.Package);
        var result = CodexProcessOwnership.SelectOwnedTargets(generation, new CodexProcessSnapshot[] { null! });

        OwnershipPolicyTestData.ExpectFailure(CodexOwnershipFailure.UnverifiableIdentity, result, "null target snapshot entry");
    }
}
