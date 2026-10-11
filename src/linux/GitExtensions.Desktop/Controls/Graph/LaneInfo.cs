// Port of src/app/GitUI/UserControls/RevisionGrid/Graph/LaneInfo.cs.
// Layout is unchanged; platform models, settings and brushes are adapted locally.
namespace GitExtensions.Desktop.Controls.Graph;

internal sealed class LaneInfo
{
    public LaneInfo(RevisionGraphSegment startSegment, RevisionGraphSegment? segmentToTheLeft, RevisionGraphSegment? segmentToTheRight = null)
    {
        StartRevision = startSegment.Child;
        Color = GetColor(colorSeed: RevisionGraphLaneColor.Hash(StartRevision.Objectid) ^ RevisionGraphLaneColor.Hash(startSegment.Parent.Objectid), segmentToTheLeft, segmentToTheRight);
    }

    public LaneInfo(RevisionGraphSegment startSegment, RevisionGraphSegment? segmentToTheLeft, RevisionGraphSegment? segmentToTheRight, LaneInfo derivedFrom)
    {
        StartRevision = startSegment.Parent;
        Color = GetColor(colorSeed: RevisionGraphLaneColor.Hash(StartRevision.Objectid), segmentToTheLeft, segmentToTheRight, derivedFrom.Color);
    }

    public int Color { get; }

    public RevisionGraphRevision StartRevision { get; }

    public int StartScore => StartRevision.Score;

    private static int GetColor(int colorSeed, RevisionGraphSegment? segmentToTheLeft, RevisionGraphSegment? segmentToTheRight, int? derivedFromColor = null)
    {
        int? leftLaneColor = segmentToTheLeft?.LaneInfo?.Color;
        int? rightLaneColor = segmentToTheRight?.LaneInfo?.Color;
        for (; ; ++colorSeed)
        {
            int color = RevisionGraphLaneColor.GetColorForLane(colorSeed);
            if (color != leftLaneColor && color != rightLaneColor && color != derivedFromColor)
            {
                return color;
            }
        }
    }
}
