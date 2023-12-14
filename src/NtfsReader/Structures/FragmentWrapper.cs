namespace System.IO.Filesystem.Ntfs;

/// <summary>
/// Adds some functionality to the basic stream.
/// </summary>
struct FragmentWrapper(Fragment fragment) : IFragment
{
    public readonly ulong Lcn => fragment.Lcn;

    public readonly ulong NextVcn => fragment.NextVcn;
}