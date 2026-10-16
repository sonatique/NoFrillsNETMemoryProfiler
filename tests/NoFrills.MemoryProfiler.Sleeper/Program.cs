// A target for the collection tests: allocates something recognizable and waits.
//
// It also builds the two halves of the stale slot check: a queue that clears the slot it removes
// from, and one that only moves its index. Both look the same from outside and hold the same
// number of items; only the second still refers to what it removed.
//
// Started with "allocate" it is the target of the GC timeline tests instead: see Allocator.
if (args is ["allocate"])
{
    // The runtime only collects gen2 in the background once it holds something worth it.
    var ballast = Enumerable.Range(0, 2000).Select(_ => new byte[10_000]).ToList();
    GC.Collect();
    GC.Collect();

    Console.WriteLine($"ready {Environment.ProcessId}");
    Console.Out.Flush();

    Allocator.Run(TimeSpan.FromMinutes(2));

    GC.KeepAlive(ballast);
    return;
}

var kept = new List<byte[]>();
for (var i = 0; i < 20; ++i) kept.Add(new byte[2_097_152]);

var tidy = new List<TidyQueue>();
var leaky = new List<LeakyQueue>();

for (var i = 0; i < 40; ++i)
{
    var one = new TidyQueue();
    var other = new LeakyQueue();

    for (var item = 0; item < 64; ++item)
    {
        one.Add(new Payload(item));
        other.Add(new Payload(item));
    }

    // Both are emptied down to two items.
    for (var item = 0; item < 62; ++item)
    {
        one.RemoveFirst();
        other.RemoveFirst();
    }

    tidy.Add(one);
    leaky.Add(other);
}

Console.WriteLine($"ready {Environment.ProcessId}");
Console.Out.Flush();

Thread.Sleep(TimeSpan.FromMinutes(2));

GC.KeepAlive(kept);
GC.KeepAlive(tidy);
GC.KeepAlive(leaky);

// Garbage of two recognizable kinds made by one named method on a thread of its own, and a
// collection induced every 100 ms on the main thread: known allocations to group and known pauses
// to pair. The rate is held to about 15 MB/s so a recording of it stays small enough to keep.
static class Allocator
{
    public static void Run(TimeSpan duration)
    {
        var end = DateTime.UtcNow + duration;

        new Thread(() => Churn(end)) { IsBackground = true }.Start();

        for (var round = 1; DateTime.UtcNow < end; ++round)
        {
            Thread.Sleep(100);

            // Every fifth one is a background gen2 instead, which stops the threads twice.
            if (round % 5 == 0) GC.Collect(2, GCCollectionMode.Forced, blocking: false);
            else GC.Collect(1, GCCollectionMode.Forced, blocking: true);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Churn(DateTime end)
    {
        var recent = new object[256];
        var next = 0;

        while (DateTime.UtcNow < end)
        {
            for (var i = 0; i < 200; ++i)
            {
                recent[next++ & 255] = new byte[1000];
                recent[next++ & 255] = new Garbage();
                recent[next++ & 255] = new Garbage();
            }

            Thread.Sleep(10);
        }
    }
}

public sealed class Garbage
{
    public long A, B, C, D, E, F, G, H;
}

sealed class Payload(int number)
{
    private readonly int m_number = number;
    private readonly byte[] m_data = new byte[512];
}

sealed class TidyQueue
{
    private readonly Payload?[] m_storage = new Payload?[64];
    private int m_length;
    private int m_head;

    public void Add(Payload item) => m_storage[(m_head + m_length++) % m_storage.Length] = item;

    public void RemoveFirst()
    {
        m_storage[m_head] = null;
        m_head = (m_head + 1) % m_storage.Length;
        --m_length;
    }
}

sealed class LeakyQueue
{
    private readonly Payload?[] m_storage = new Payload?[64];
    private int m_length;
    private int m_head;

    public void Add(Payload item) => m_storage[(m_head + m_length++) % m_storage.Length] = item;

    // The slot keeps the item: this is the shape the detector has to notice.
    public void RemoveFirst()
    {
        m_head = (m_head + 1) % m_storage.Length;
        --m_length;
    }
}
