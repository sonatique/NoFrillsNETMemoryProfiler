namespace NoFrills.MemoryProfiler.Core;

/// <summary>
/// A collection that did not produce a usable dump. Separate from the other failures because it
/// is usually transient: the same process dumps fine a minute later.
/// </summary>
public sealed class HeapCollectionException : Exception
{
    public HeapCollectionException(string message) : base(message)
    {
    }

    public HeapCollectionException(string message, Exception inner) : base(message, inner)
    {
    }
}
