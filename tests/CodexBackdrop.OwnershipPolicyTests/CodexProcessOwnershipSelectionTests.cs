using CodexBackdrop;

internal static class CodexProcessOwnershipSelectionTests
{
    public static void RunAll()
    {
        SelectsExactlyOneVerifiedVisibleRoot();
        RejectsNoVisibleRoot();
        RejectsMultipleVisibleRoots();
        RejectsVisibleHelperAsRoot();
        RejectsVisibleRootWithParentAttribution();
        RejectsMissingIdentityFields();
        RejectsMismatchedIdentityFields();
        RejectsMixedSessionIdentities();
        RejectsMixedOwnerSids();
        RejectsInvalidPackageContracts();
        RejectsInvalidProcessIdentity();
        RejectsInvalidSnapshotEntries();
        NormalizesEquivalentExecutablePaths();
    }

    private static void SelectsExactlyOneVerifiedVisibleRoot()
    {
        var root = OwnershipPolicyTestData.Root();
        var result = CodexProcessOwnership.SelectGeneration([root], OwnershipPolicyTestData.Package);

        OwnershipPolicyTestData.Expect(result.Accepted, "verified visible root was rejected");
        OwnershipPolicyTestData.Expect(result.Generation?.Root == root.Identity, "selected generation recorded the wrong root");
        OwnershipPolicyTestData.Expect(result.Generation?.Package == OwnershipPolicyTestData.Package, "selected generation lost package attestation");
    }

    private static void RejectsNoVisibleRoot()
    {
        var helper = OwnershipPolicyTestData.Helper(11, OwnershipPolicyTestData.Identity(10, 10));
        var result = CodexProcessOwnership.SelectGeneration([helper], OwnershipPolicyTestData.Package);

        OwnershipPolicyTestData.ExpectFailure(CodexOwnershipFailure.NoVisibleRoot, result, "helper-only launch");
    }

    private static void RejectsMultipleVisibleRoots()
    {
        var result = CodexProcessOwnership.SelectGeneration(
            [OwnershipPolicyTestData.Root(), OwnershipPolicyTestData.Root(12, 11)],
            OwnershipPolicyTestData.Package);

        OwnershipPolicyTestData.ExpectFailure(CodexOwnershipFailure.AmbiguousVisibleRoots, result, "multiple visible roots");
    }

    private static void RejectsVisibleHelperAsRoot()
    {
        var helper = OwnershipPolicyTestData.Helper(11, OwnershipPolicyTestData.Identity(10, 10), visible: true);
        var result = CodexProcessOwnership.SelectGeneration([helper], OwnershipPolicyTestData.Package);

        OwnershipPolicyTestData.ExpectFailure(CodexOwnershipFailure.UnverifiableIdentity, result, "visible helper root");
    }

    private static void RejectsVisibleRootWithParentAttribution()
    {
        var root = OwnershipPolicyTestData.Root() with
        {
            ParentIdentity = OwnershipPolicyTestData.Identity(9, 1),
        };
        var result = CodexProcessOwnership.SelectGeneration([root], OwnershipPolicyTestData.Package);

        OwnershipPolicyTestData.ExpectFailure(
            CodexOwnershipFailure.UnverifiableIdentity,
            result,
            "visible root with parent attribution");
    }

    private static void RejectsMissingIdentityFields()
    {
        var fields = new (string Name, CodexProcessSnapshot Process)[]
        {
            ("package", OwnershipPolicyTestData.Root(package: null)),
            ("publisher", OwnershipPolicyTestData.Root(publisher: null)),
            ("path", OwnershipPolicyTestData.Root(executable: null)),
        };

        foreach (var field in fields)
        {
            var process = field.Process with
            {
                PackageIdentity = field.Name == "package" ? null : field.Process.PackageIdentity,
                PublisherIdentity = field.Name == "publisher" ? null : field.Process.PublisherIdentity,
                ExecutablePath = field.Name == "path" ? null : field.Process.ExecutablePath,
            };
            var result = CodexProcessOwnership.SelectGeneration([process], OwnershipPolicyTestData.Package);
            OwnershipPolicyTestData.ExpectFailure(CodexOwnershipFailure.UnverifiableIdentity, result, $"missing {field.Name}");
        }
    }

    private static void RejectsMismatchedIdentityFields()
    {
        var fields = new (string Name, CodexProcessSnapshot Process)[]
        {
            ("package", OwnershipPolicyTestData.Root(package: "other-package")),
            ("publisher", OwnershipPolicyTestData.Root(publisher: "CN=Other Publisher")),
            ("path", OwnershipPolicyTestData.Root(executable: Path.Combine(Path.GetTempPath(), "other.exe"))),
        };

        foreach (var field in fields)
        {
            var result = CodexProcessOwnership.SelectGeneration([field.Process], OwnershipPolicyTestData.Package);
            OwnershipPolicyTestData.ExpectFailure(CodexOwnershipFailure.UnverifiableIdentity, result, $"mismatched {field.Name}");
        }
    }

    private static void RejectsMixedSessionIdentities()
    {
        var result = CodexProcessOwnership.SelectGeneration(
            [
                OwnershipPolicyTestData.Root(),
                OwnershipPolicyTestData.Root(12, 11, sessionId: OwnershipPolicyTestData.SessionId + 1),
            ],
            OwnershipPolicyTestData.Package);

        OwnershipPolicyTestData.ExpectFailure(
            CodexOwnershipFailure.UnverifiableIdentity,
            result,
            "mixed process sessions");
    }

    private static void RejectsMixedOwnerSids()
    {
        var result = CodexProcessOwnership.SelectGeneration(
            [
                OwnershipPolicyTestData.Root(),
                OwnershipPolicyTestData.Root(12, 11, userSid: "S-1-5-21-100-200-300-1002"),
            ],
            OwnershipPolicyTestData.Package);

        OwnershipPolicyTestData.ExpectFailure(
            CodexOwnershipFailure.UnverifiableIdentity,
            result,
            "mixed process owner sids");
    }

    private static void RejectsInvalidPackageContracts()
    {
        var invalid = new CodexPackageContract?[]
        {
            null,
            new(null, OwnershipPolicyTestData.Package.PublisherIdentity, OwnershipPolicyTestData.Package.ExecutablePath),
            new(OwnershipPolicyTestData.Package.PackageIdentity, null, OwnershipPolicyTestData.Package.ExecutablePath),
            new(OwnershipPolicyTestData.Package.PackageIdentity, OwnershipPolicyTestData.Package.PublisherIdentity, null),
            new(OwnershipPolicyTestData.Package.PackageIdentity, OwnershipPolicyTestData.Package.PublisherIdentity, "relative\\ChatGPT.exe"),
        };

        foreach (var package in invalid)
        {
            var result = CodexProcessOwnership.SelectGeneration([OwnershipPolicyTestData.Root()], package);
            OwnershipPolicyTestData.ExpectFailure(CodexOwnershipFailure.InvalidPackageContract, result, "invalid package contract");
        }
    }

    private static void RejectsInvalidProcessIdentity()
    {
        var invalidPid = OwnershipPolicyTestData.Root() with
        {
            Identity = OwnershipPolicyTestData.Identity(0, 10),
        };
        var invalidStart = OwnershipPolicyTestData.Root() with
        {
            Identity = new CodexProcessIdentity(10, default),
        };

        foreach (var process in new[] { invalidPid, invalidStart })
        {
            var result = CodexProcessOwnership.SelectGeneration([process], OwnershipPolicyTestData.Package);
            OwnershipPolicyTestData.ExpectFailure(CodexOwnershipFailure.UnverifiableIdentity, result, "invalid root identity");
        }
    }

    private static void RejectsInvalidSnapshotEntries()
    {
        var result = CodexProcessOwnership.SelectGeneration(
            new CodexProcessSnapshot[] { null! },
            OwnershipPolicyTestData.Package);

        OwnershipPolicyTestData.ExpectFailure(CodexOwnershipFailure.UnverifiableIdentity, result, "null snapshot entry");
    }

    private static void NormalizesEquivalentExecutablePaths()
    {
        var equivalent = Path.Combine(Path.GetDirectoryName(OwnershipPolicyTestData.ExecutablePath)!, "..", "app", "ChatGPT.exe");
        var root = OwnershipPolicyTestData.Root(executable: equivalent);
        var result = CodexProcessOwnership.SelectGeneration([root], OwnershipPolicyTestData.Package);

        OwnershipPolicyTestData.Expect(result.Accepted, "equivalent absolute executable path was rejected");
    }
}
