namespace KetPsi.Extensions.Logging.Console.Internals;

internal static class ScopeNodePool
{
    // 64 pooled scopes are more than enough for high-concurrency workloads
    private static readonly ScopeNode?[] s_pool = new ScopeNode?[64];

    public static ScopeNode Rent()
    {
        for (int i = 0; i < s_pool.Length; i++)
        {
            var node = s_pool[i];
            if (node != null && Interlocked.CompareExchange(ref s_pool[i], null, node) == node)
            {
                return node;
            }
        }
        return new ScopeNode(); // Fallback if saturated during extreme spikes
    }

    public static void Return(ScopeNode node)
    {
        node.Reset();
        for (int i = 0; i < s_pool.Length; i++)
        {
            if (s_pool[i] == null && Interlocked.CompareExchange(ref s_pool[i], node, null) == null)
            {
                return;
            }
        }
    }
}