using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

internal sealed class AskpassProtocolTests
{
    [TestCase("Username for 'https://example.invalid/(yes/no)': ", null, AskpassPromptKind.Username)]
    [TestCase("Username: ", null, AskpassPromptKind.Username)]
    [TestCase("Username@fixture.invalid's password: ", null, AskpassPromptKind.Secret)]
    [TestCase("user@(yes/no).invalid's password: ", null, AskpassPromptKind.Secret)]
    [TestCase("Enter user@(yes/no).invalid's old password: ", null, AskpassPromptKind.Secret)]
    [TestCase("Enter user@(yes/no).invalid's new password: ", null, AskpassPromptKind.Secret)]
    [TestCase("Retype user@(yes/no).invalid's new password: ", null, AskpassPromptKind.Secret)]
    [TestCase("Bad passphrase, try again for /tmp/key-(yes/no): ", null, AskpassPromptKind.Secret)]
    [TestCase("Enter old passphrase for /tmp/key-(yes/no): ", null, AskpassPromptKind.Secret)]
    [TestCase("Enter new passphrase (yes/no): ", null, AskpassPromptKind.Secret)]
    [TestCase("Enter same passphrase again (yes/no): ", null, AskpassPromptKind.Secret)]
    [TestCase("UsernameSecret request", null, AskpassPromptKind.Secret)]
    [TestCase("Password for 'https://example.invalid/(yes/no)': ", null, AskpassPromptKind.Secret)]
    [TestCase("Enter passphrase for key '/fake/(yes/no/[fingerprint])': ", null, AskpassPromptKind.Secret)]
    [TestCase("Enter PIN for user presence", null, AskpassPromptKind.Secret)]
    [TestCase("PIN for key '/fake/key':", null, AskpassPromptKind.Secret)]
    [TestCase("Unknown authentication request", null, AskpassPromptKind.Secret)]
    [TestCase("Are you sure you want to continue connecting (yes/no)?", null, AskpassPromptKind.YesNo)]
    [TestCase("Continue (YES/NO)?", null, AskpassPromptKind.YesNo)]
    [TestCase("Host fingerprint\nContinue (yes/no/[fingerprint])?", null, AskpassPromptKind.HostVerification)]
    [TestCase("Please type 'yes', 'no' or the fingerprint: ", null, AskpassPromptKind.HostVerification)]
    [TestCase("Please type 'yes' or 'no': ", null, AskpassPromptKind.YesNo)]
    [TestCase("Password for fake key", "confirm", AskpassPromptKind.Permission)]
    [TestCase("Continue (yes/no)?", "none", AskpassPromptKind.Notification)]
    [TestCase("Unknown authentication request", "CONFIRM", AskpassPromptKind.Secret)]
    [TestCase("Unknown authentication request", "unknown", AskpassPromptKind.Secret)]
    public void NativeHintsAndKnownPromptsSelectTheUiWithoutChangingThePrompt(string prompt, string? hint, AskpassPromptKind expected)
    {
        AskpassRequest request = AskpassRequest.Create(prompt, hint);
        Assert.That(request.Kind, Is.EqualTo(expected));
        Assert.That(request.Prompt, Is.EqualTo(prompt));
    }

    [Test]
    public void ArgumentBoundaryDistinguishesDefaultEmptyAndInvalidArity()
    {
        Assert.That(AskpassApplication.CreateRequest([], null), Is.EqualTo(new AskpassRequest("Enter your OpenSSH passphrase:", AskpassPromptKind.Secret)));
        Assert.That(AskpassApplication.CreateRequest([""], null), Is.EqualTo(new AskpassRequest("", AskpassPromptKind.Secret)));
        const string prompt = "literal λ '$() & \"quotes\"\nsecond line";
        Assert.That(AskpassApplication.CreateRequest([prompt], "confirm"), Is.EqualTo(new AskpassRequest(prompt, AskpassPromptKind.Permission)));
        Assert.That(AskpassApplication.CreateRequest(["one", "two"], null), Is.Null);
    }

    [TestCase(AskpassPromptKind.Secret, AskpassDecision.Submit, " fake λ '$() & ", " fake λ '$() & \n", 0)]
    [TestCase(AskpassPromptKind.Secret, AskpassDecision.Submit, "", "\n", 0)]
    [TestCase(AskpassPromptKind.Username, AskpassDecision.Submit, "user λ", "user λ\n", 0)]
    [TestCase(AskpassPromptKind.YesNo, AskpassDecision.Yes, null, "yes", 0)]
    [TestCase(AskpassPromptKind.YesNo, AskpassDecision.No, null, "no", 0)]
    [TestCase(AskpassPromptKind.HostVerification, AskpassDecision.Yes, null, "yes", 0)]
    [TestCase(AskpassPromptKind.HostVerification, AskpassDecision.No, null, "no", 0)]
    [TestCase(AskpassPromptKind.HostVerification, AskpassDecision.Submit, "SHA256:fake+Fingerprint/AbC", "SHA256:fake+Fingerprint/AbC", 0)]
    [TestCase(AskpassPromptKind.HostVerification, AskpassDecision.Submit, "MD5:01:AB:cd:23", "MD5:01:AB:cd:23", 0)]
    [TestCase(AskpassPromptKind.Permission, AskpassDecision.Yes, null, "", 0)]
    [TestCase(AskpassPromptKind.Permission, AskpassDecision.No, null, "", 1)]
    [TestCase(AskpassPromptKind.Notification, AskpassDecision.Dismissed, null, "", 0)]
    public void WireContractPreservesExactResponsesAndOpenSshDecisionStatus(AskpassPromptKind kind, AskpassDecision decision, string? value, string expectedOutput, int expectedCode)
    {
        AskpassRequest request = new("fake public prompt", kind);
        AskpassResult result = new(decision, value);
        using StringWriter output = new();
        Assert.That(AskpassApplication.WriteResult(request, result, output), Is.EqualTo(expectedCode));
        Assert.That(AskpassProtocol.GetExitCode(request, result), Is.EqualTo(expectedCode));
        Assert.That(output.ToString(), Is.EqualTo(expectedOutput));
    }

    [TestCase("line\nbreak")]
    [TestCase("carriage\rreturn")]
    [TestCase("nul\0byte")]
    [TestCase(null)]
    public void InvalidResponseIsRejectedBeforeAnyBytesAreWritten(string? value)
    {
        foreach (AskpassPromptKind kind in new[] { AskpassPromptKind.Secret, AskpassPromptKind.Username, AskpassPromptKind.HostVerification })
        {
            using StringWriter output = new();
            Assert.That(AskpassApplication.WriteResult(new("fake", kind), new(AskpassDecision.Submit, value), output), Is.EqualTo(1));
            Assert.That(output.ToString(), Is.Empty);
        }
    }

    [Test]
    public void CancelAndWrongModeDecisionsNeverAccidentallyGrantPermission()
    {
        foreach (AskpassPromptKind kind in Enum.GetValues<AskpassPromptKind>())
        {
            foreach (AskpassDecision decision in new[] { AskpassDecision.Cancelled, (AskpassDecision)999 })
            { AssertRejected(new("fake", kind), new(decision)); }
        }
        foreach (AskpassPromptKind kind in new[] { AskpassPromptKind.Secret, AskpassPromptKind.Username, AskpassPromptKind.Notification })
        {
            AssertRejected(new("fake", kind), new(AskpassDecision.Yes));
            AssertRejected(new("fake", kind), new(AskpassDecision.No));
        }
        foreach (AskpassPromptKind kind in new[] { AskpassPromptKind.YesNo, AskpassPromptKind.Permission, AskpassPromptKind.Notification })
        {
            AssertRejected(new("fake", kind), new(AskpassDecision.Submit, "yes"));
            AssertRejected(new("fake", kind), new(AskpassDecision.Submit, ""));
        }
        foreach (AskpassPromptKind kind in new[] { AskpassPromptKind.Secret, AskpassPromptKind.Username, AskpassPromptKind.YesNo, AskpassPromptKind.HostVerification, AskpassPromptKind.Permission })
        { AssertRejected(new("fake", kind), new(AskpassDecision.Dismissed)); }
        AssertRejected(new("fake", (AskpassPromptKind)999), new(AskpassDecision.Yes));
        AssertRejected(new("fake", AskpassPromptKind.Permission), new(AskpassDecision.Yes, "unexpected data"));
    }

    [TestCase("")]
    [TestCase(" \t ")]
    public void BlankFingerprintIsNotAnApproval(string value)
        => AssertRejected(new("fake", AskpassPromptKind.HostVerification), new(AskpassDecision.Submit, value));

    private static void AssertRejected(AskpassRequest request, AskpassResult result)
    {
        using StringWriter output = new();
        Assert.That(AskpassApplication.WriteResult(request, result, output), Is.EqualTo(1));
        Assert.That(AskpassProtocol.GetExitCode(request, result), Is.EqualTo(1));
        Assert.That(output.ToString(), Is.Empty);
    }
}
