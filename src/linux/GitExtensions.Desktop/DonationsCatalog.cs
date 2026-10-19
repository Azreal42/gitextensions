namespace GitExtensions.Linux.Desktop;

/// <summary>The destination and explanation from the original FormDonate; no payment is made by this window.</summary>
internal static class DonationsCatalog
{
    internal static readonly Uri DonationUri = new("https://opencollective.com/gitextensions");
    internal static readonly Uri BadgeAsset = new("avares://GitExtensions/Assets/Icons/Donate.png");
    internal const string Explanation = "We have a dedicated team of collaborators that spends a lot of time maintaining the app, working on new features and fixing bugs." +
        "You can support the project by making a financial contribution. Donations will be used to cover running costs " +
        "and to get the resources needed to keep the project running. We will also use donations to thank collaborators for their efforts.\r\n\r\n" +
        "Click on the button below to get more information about making a donation.";
}
