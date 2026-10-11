// Port of src/app/GitUI/UserControls/RevisionGrid/Graph/Lane.cs.
// Layout is unchanged; platform models, settings and brushes are adapted locally.
namespace GitExtensions.Desktop.Controls.Graph;

internal readonly record struct Lane(int Index, LaneSharing Sharing);
