// A target for the collection tests: allocates something recognizable and waits.
//
// It also builds the two halves of the stale slot check: a queue that clears the slot it removes
// from, and one that only moves its index. Both look the same from outside and hold the same
// number of items; only the second still refers to what it removed.
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
