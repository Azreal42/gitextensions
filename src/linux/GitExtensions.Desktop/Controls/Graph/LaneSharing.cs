// Port of src/app/GitUI/UserControls/RevisionGrid/Graph/LaneSharing.cs.
// Layout is unchanged; platform models, settings and brushes are adapted locally.
namespace GitExtensions.Desktop.Controls.Graph;

internal enum LaneSharing
{
    /// <summary>
    /// The graph segment uses the lane exclusively or is the initial user of the lane.
    /// </summary>
    ExclusiveOrPrimary,

    /// <summary>
    /// The graph segment entirely re-uses a lane with another one because they have the same parent.
    /// </summary>
    Entire,

    /// <summary>
    /// The graph segment partially re-uses a lane with another one because they have the same parent.
    /// </summary>
    DifferentStart,

    /// <summary>
    /// The graph segment partially re-uses a lane with another one because they have the same child.
    /// </summary>
    DifferentEnd
}
